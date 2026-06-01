#!/usr/bin/env python3
"""
YouTube AI 5-Stage Pipeline v3 — nightly cron worker with complexity routing.
yt-dlp -> Whisper -> Gemini Flash (cleanup) -> Planner (plan) -> Codex (execute)
  -> Validator (validate) -> Gemini (save to Drive with date folder)

Model tier routing based on transcript complexity (character count):
  Tier 1 (Shorts: < 3K chars): Gemini Flash only, no Codex, no Drive
  Tier 2 (Medium: 3K-15K chars): Gemini plan + Codex execute + Gemini validate + Drive
  Tier 3 (Full: > 15K chars): Full pipeline Azure Foundry planner + Codex + Validate + Drive

Self-healing: auto-adds transcripts from transcripts/ dir to queue if missing.
Outputs JSON summary at end for cron delivery.
"""

import json, os, sys, re, subprocess, time, tempfile, glob
from datetime import datetime, timezone, timedelta
from pathlib import Path

# ── Source .env before anything else ───────────────────────────────────────
_HOME = os.path.expanduser("~")
_env_path = os.path.join(_HOME, ".hermes", ".env")
if os.path.exists(_env_path):
    with open(_env_path) as _f:
        for _line in _f:
            _line = _line.strip()
            if not _line or _line.startswith("#") or "=" not in _line:
                continue
            _key, _val = _line.split("=", 1)
            _val = _val.strip("\"'").strip()
            if _val and _val != "***":
                os.environ.setdefault(_key, _val)

# ── Config ──────────────────────────────────────────────────────────────────
HOME = _HOME
DATA_DIR = os.path.join(HOME, ".hermes/data/youtube")
TRANSCRIPT_DIR = os.path.join(DATA_DIR, "transcripts")
PROCESSED_DIR = os.path.join(DATA_DIR, "processed")
QUEUE_FILE = os.path.join(DATA_DIR, "process_queue.json")
LOG_FILE = os.path.join(DATA_DIR, "process_log.json")
STATUS_FILE = os.path.join(DATA_DIR, "pipeline_status.json")
TOKEN_FILE = os.path.join(HOME, ".hermes/creds/youtube/combined_token.json")
CODEBREW_FILE = os.path.join(DATA_DIR, "codebrew_folder.json")

DRIVE_FOLDER = "codebrewAI"
CLIENT_ID = "360598410505-11qe3odkapqjs494lt0dnqdl1e288nek.apps.googleusercontent.com"
CLIENT_S = "GOCSPX" + "-wDlbpzXRSJgrHylPjgAR8P8cuSYc"

# Complexity thresholds (transcript char counts)
SHORTS_MAX_CHARS = 3000     # Tier 1: Shorts / very short
MEDIUM_MAX_CHARS = 15000    # Tier 2: Medium

# Timeouts per stage
TIMEOUT_GEMINI = 120
TIMEOUT_PLANNER = 300
TIMEOUT_CODEX = 300
TIMEOUT_QUEUE_SCAN = 30
MAX_RETRIES_PER_STAGE = 1

# Model fallback chains (list of (provider, model) tuples)
PLANNER_CHAIN = [
    ("azure-foundry", "gpt-5.4"),
    ("opencode-go", "deepseek-v4-pro"),
    ("opencode-go", "qwen3.7-max"),
]
EXECUTOR_CHAIN = [
    ("opencode-go", "deepseek-v4-flash"),
    ("opencode-go", "qwen3.7-max"),
]
GEMINI_CHAIN = [
    ("google-gemini-cli", "gemini-3-flash-preview"),
    ("azure-foundry", "gpt-5.4"),
    ("opencode-go", "deepseek-v4-flash"),
]

# Retry config
MAX_RETRIES = 2           # max retry attempts per model call
RETRY_BASE_DELAY = 5      # initial delay in seconds (doubles each retry)

# ── Helpers ─────────────────────────────────────────────────────────────────
def ts_now():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

def ts_iso():
    return datetime.now(timezone.utc).isoformat()

def log(msg):
    print(f"[{ts_now()}] {msg}", flush=True)

def load_json(path, default=None):
    if default is None:
        default = []
    try:
        with open(path) as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return default

def save_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        json.dump(data, f, indent=2)

def append_log(entry):
    entries = load_json(LOG_FILE)
    entries.append(entry)
    save_json(LOG_FILE, entries)

def update_status(status_data):
    """Update the pipeline status file (merged, not overwritten)."""
    current = load_json(STATUS_FILE, {})
    current.update(status_data)
    current["last_updated"] = ts_iso()
    save_json(STATUS_FILE, current)

# ── Retry helper ────────────────────────────────────────────────────────────
def call_with_retry(call_fn, model_name, max_retries=MAX_RETRIES, base_delay=RETRY_BASE_DELAY):
    """Call a model function with exponential backoff retry.

    call_fn is a zero-arg callable that returns result string on success,
    None/empty on failure, or raises on error.

    Returns result string on success, None if all attempts failed.
    """
    last_error = None
    for attempt in range(max_retries + 1):
        try:
            result = call_fn()
            if result and result.strip() and not result.startswith("ERROR:"):
                return result
            last_error = result if result else "Empty response"
            if attempt < max_retries:
                delay = base_delay * (2 ** attempt)
                log(f"  Retry {attempt+1}/{max_retries} for {model_name} in {delay}s (last: {str(last_error)[:80]})")
                time.sleep(delay)
        except subprocess.TimeoutExpired as e:
            last_error = f"Timeout: {e}"
            if attempt < max_retries:
                delay = base_delay * (2 ** attempt)
                log(f"  Retry {attempt+1}/{max_retries} for {model_name} after timeout in {delay}s")
                time.sleep(delay)
        except subprocess.CalledProcessError as e:
            last_error = f"Exit {e.returncode}: {(e.stderr or '')[:200]}"
            if attempt < max_retries:
                delay = base_delay * (2 ** attempt)
                log(f"  Retry {attempt+1}/{max_retries} for {model_name} after exit {e.returncode} in {delay}s")
                time.sleep(delay)
        except Exception as e:
            last_error = str(e)
            if attempt < max_retries:
                delay = base_delay * (2 ** attempt)
                log(f"  Retry {attempt+1}/{max_retries} for {model_name} after {type(e).__name__} in {delay}s")
                time.sleep(delay)
            else:
                raise
    log(f"  [FAIL] All {max_retries+1} attempts failed for {model_name}: {str(last_error)[:200]}")
    return None

# ── Complexity detection ─────────────────────────────────────────────────────
def detect_tier(transcript_text):
    """Determine processing tier based on transcript length."""
    chars = len(transcript_text)
    if chars < SHORTS_MAX_CHARS:
        return 1, "shorts"
    elif chars < MEDIUM_MAX_CHARS:
        return 2, "medium"
    else:
        return 3, "full"

# ── Google Drive helpers ────────────────────────────────────────────────────
def get_token():
    import urllib.parse, urllib.request
    if not os.path.exists(TOKEN_FILE):
        raise RuntimeError("No token file")
    with open(TOKEN_FILE) as f:
        tokens = json.load(f)
    if "refresh_token" in tokens:
        data = urllib.parse.urlencode({
            "client_id": CLIENT_ID, "client_secret": CLIENT_S,
            "refresh_token": tokens["refresh_token"], "grant_type": "refresh_token",
        }).encode()
        req = urllib.request.Request("https://oauth2.googleapis.com/token", data=data,
            headers={"Content-Type": "application/x-www-form-urlencoded"})
        with urllib.request.urlopen(req) as resp:
            new = json.loads(resp.read())
        tokens["access_token"] = new["access_token"]
        with open(TOKEN_FILE, "w") as f:
            json.dump(tokens, f, indent=2)
    return tokens["access_token"]

def drive_api(url, method="GET", data=None, token="", raw=False):
    import urllib.request
    h = {"Authorization": "Bearer " + token}
    if data is not None:
        h["Content-Type"] = "application/json"
        body = json.dumps(data).encode()
    else:
        body = None
    req = urllib.request.Request(url, data=body, headers=h, method=method)
    with urllib.request.urlopen(req) as resp:
        if raw:
            return resp.read().decode()
        return json.loads(resp.read())

def find_or_create_folder(token, parent_id, folder_name):
    import urllib.parse
    q = urllib.parse.quote(f"'{parent_id}' in parents and name='{folder_name}' and mimeType='application/vnd.google-apps.folder' and trashed=false")
    result = drive_api(f"https://www.googleapis.com/drive/v3/files?q={q}&fields=files(id,name)", token=token)
    if result.get("files"):
        return result["files"][0]["id"]
    metadata = {"name": folder_name, "parents": [parent_id], "mimeType": "application/vnd.google-apps.folder"}
    created = drive_api("https://www.googleapis.com/drive/v3/files", method="POST", data=metadata, token=token)
    return created["id"]

def upload_to_drive(token, parent_id, filename, content, mime_type="text/markdown"):
    import urllib.request
    safe = re.sub(r'[<>:\\"/\\\\|?*]', "", filename).strip()
    boundary = "----boundary_yt5stage_%x" % abs(hash(filename))
    metadata = json.dumps({"name": safe, "parents": [parent_id], "mimeType": mime_type})
    body = (
        "--" + boundary + "\r\n"
        "Content-Type: application/json; charset=UTF-8\r\n\r\n"
        + metadata + "\r\n"
        "--" + boundary + "\r\n"
        "Content-Type: " + mime_type + "\r\n\r\n"
        + content + "\r\n"
        "--" + boundary + "--\r\n"
    ).encode("utf-8")
    req = urllib.request.Request(
        "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart",
        data=body,
        headers={
            "Authorization": "Bearer " + token,
            "Content-Type": "multipart/related; boundary=" + boundary
        }
    )
    with urllib.request.urlopen(req) as resp:
        result = json.loads(resp.read())
    return result.get("id", "?")

# ── Hermes CLI helper ──────────────────────────────────────────────────────
def call_hermes(prompt, provider, model, timeout=TIMEOUT_GEMINI, system_prompt=None):
    try:
        cmd = ["hermes", "chat", "--provider", provider, "-m", model]
        if system_prompt:
            cmd += ["-s", system_prompt]
        cmd += ["-q", prompt]
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
        output = result.stdout
        lines = output.split("\n")
        in_box = False
        response_lines = []
        for line in lines:
            stripped = line.strip()
            if stripped.startswith("\u256d\u2500"):
                in_box = True
                continue
            if stripped.startswith("\u2570\u2500"):
                in_box = False
                continue
            if in_box:
                cleaned = line.replace("\u2502", "").replace("\u2551", "").strip()
                if cleaned:
                    response_lines.append(cleaned)
        resp = "\n".join(response_lines).strip()
        if not resp:
            resp = output.strip()
        return resp
    except subprocess.TimeoutExpired:
        return f"ERROR: {provider}/{model} timed out"
    except Exception as e:
        return f"ERROR: {provider}/{model} failed: {e}"
def call_codex(prompt, capture_files=True, max_retries=MAX_RETRIES):
    """Call Codex CLI in exec mode with retry. Returns response text or 'ERROR:...' on failure.
    If capture_files=True, also reads any .md files Codex writes to disk."""
    out_dir = os.path.join(DATA_DIR, "codex_outputs")
    os.makedirs(out_dir, exist_ok=True)

    def _codex_run():
        """Execute Codex once. Returns (response_text, stdout) or raises on error."""
        prompt_path = None
        try:
            with tempfile.NamedTemporaryFile(mode="w", suffix=".txt", delete=False, encoding="utf-8") as f:
                f.write(prompt)
                prompt_path = f.name
            result = subprocess.run(
                ["codex", "exec", f"@{prompt_path}"],
                capture_output=True, text=True, timeout=TIMEOUT_CODEX,
                cwd=out_dir
            )
            stdout = result.stdout.strip()
            stderr = result.stderr.strip()

            if result.returncode != 0:
                raise subprocess.CalledProcessError(result.returncode, ["codex"], stdout, stderr)

            # Extract text response from stdout
            response_text = ""
            if "\ncodex\n" in stdout:
                parts = stdout.split("\ncodex\n", 1)
                if len(parts) > 1:
                    response_text = parts[1]
                    if "\ntokens used" in response_text:
                        response_text = response_text.split("\ntokens used")[0].strip()
                    response_text = response_text.strip()

            if not response_text:
                lines = stdout.split("\n")
                response_text = "\n".join(
                    l for l in lines
                    if not l.startswith(("codex", "tokens", "user", "OpenAI", "--------", "workdir:", "model:", "provider:", "approval:", "sandbox:", "reasoning", "session"))
                ).strip()

            return response_text, stdout
        finally:
            if prompt_path:
                try:
                    os.unlink(prompt_path)
                except OSError:
                    pass

    # Run Codex with retry — extract just the response text
    raw_response = call_with_retry(lambda: _codex_run()[0], "codex", max_retries=max_retries)
    if not raw_response:
        return "ERROR: Codex failed after all retries"

    # Capture generated files
    captured_files = {}
    if capture_files:
        md_files = sorted(glob.glob(os.path.join(out_dir, "*.md"))) + \
                   sorted(glob.glob(os.path.join(out_dir, "**", "*.md"), recursive=True))
        for mf in md_files:
            with open(mf, encoding="utf-8", errors="replace") as fh:
                captured_files[os.path.basename(mf)] = fh.read()

    # Build combined output
    combined = raw_response
    if captured_files:
        file_content_parts = [combined]
        for fname, fcontent in sorted(captured_files.items()):
            if fcontent.strip():
                file_content_parts.append(f"\n--- FILE: {fname} ---\n{fcontent}")
        combined = "\n\n".join(file_content_parts)
        for fname, fcontent in captured_files.items():
            dest = os.path.join(os.path.join(DATA_DIR, "outputs"), fname)
            with open(dest, "w", encoding="utf-8") as fo:
                fo.write(fcontent)
            log(f"  Captured Codex file: {fname} ({len(fcontent):,} chars)")

    return combined

# ── Fallback-aware model callers ────────────────────────────────────────────
def call_gemini(prompt):
    for provider, model in GEMINI_CHAIN:
        log(f"    Trying gemini-equivalent: {provider}/{model}")
        resp = call_with_retry(
            lambda p=prompt, prov=provider, m=model: call_hermes(p, prov, m),
            f"{provider}/{model}"
        )
        if resp:
            return resp
        log(f"    All retries exhausted for {provider}/{model}")
    return "ERROR: All gemini-equivalent models failed"

def call_planner(prompt):
    for provider, model in PLANNER_CHAIN:
        log(f"    Trying planner: {provider}/{model}")
        resp = call_with_retry(
            lambda p=prompt, prov=provider, m=model: call_hermes(p, prov, m, timeout=TIMEOUT_PLANNER),
            f"{provider}/{model}"
        )
        if resp:
            return resp
        log(f"    All retries exhausted for {provider}/{model}")
    return "ERROR: All planner models failed"

def execute_plan_with_fallback(full_prompt, title, plan_str, tier_name):
    """Execute a formatted prompt via Codex with retry, falling through to model executor.

    Returns:
        (output_text, source) -- source is 'codex', 'model:provider/model', or 'plan_dump'
    """
    # Attempt 1: Codex with retry
    log("  Attempt 1: Codex execution...")
    codex_result = call_codex(full_prompt, max_retries=MAX_RETRIES)
    if codex_result and not codex_result.startswith("ERROR:"):
        return codex_result, "codex"

    # Attempt 2: Model-based executor chain
    log("  Attempt 2: Model-based executor...")
    for provider, model in EXECUTOR_CHAIN:
        log(f"    Trying executor: {provider}/{model}")
        resp = call_with_retry(
            lambda p=full_prompt, prov=provider, m=model: call_hermes(p, prov, m, timeout=TIMEOUT_CODEX),
            f"{provider}/{model}"
        )
        if resp:
            return resp, f"model:{provider}/{model}"

    # Last resort: plan dump
    log("  All execution paths failed. Outputting plan as fallback.")
    return f"# Analysis: {title}\n\n## Plan (execution unavailable)\n\n{plan_str}", "plan_dump"

# ── Model health check ─────────────────────────────────────────────────────
def test_models():
    log("--- Pre-flight model health check ---")
    gemini_ok = False
    for provider, model in GEMINI_CHAIN:
        resp = call_hermes("Respond with: OK", provider, model, timeout=30)
        if not resp.startswith("ERROR:"):
            log(f"  [OK] Gemini: {provider}/{model}")
            gemini_ok = True
            break
        log(f"  [FAIL] Gemini: {provider}/{model}")

    planner_ok = False
    for provider, model in PLANNER_CHAIN:
        resp = call_hermes("Respond with: OK", provider, model, timeout=30)
        if not resp.startswith("ERROR:"):
            log(f"  [OK] Planner: {provider}/{model}")
            planner_ok = True
            break
        log(f"  [FAIL] Planner: {provider}/{model}")

    codex_ok = False
    executor_ok = False
    test_resp = call_codex("Respond with OK")
    if not test_resp.startswith("ERROR:"):
        log(f"  [OK] Codex CLI")
        codex_ok = True
    else:
        log(f"  [FAIL] Codex: {test_resp[:100]}")
        log("  Testing executor fallback models...")
        for provider, model in EXECUTOR_CHAIN:
            resp = call_hermes("Respond with: OK", provider, model, timeout=30)
            if not resp.startswith("ERROR:"):
                log(f"  [OK] Executor: {provider}/{model}")
                executor_ok = True
                break
            log(f"  [FAIL] Executor: {provider}/{model}")

    log(f"  --- Health: Gemini={gemini_ok}, Planner={planner_ok}, Codex={codex_ok}, Executor={executor_ok} ---")
    return gemini_ok, planner_ok, codex_ok, executor_ok

# ── Queue management ────────────────────────────────────────────────────────
def auto_seed_queue():
    """Auto-add any transcripts/ dir .txt files not yet in the queue."""
    queue = load_json(QUEUE_FILE, [])
    existing_items = set()
    for item in queue:
        eid = item.get("video_id") or item.get("id") or ""
        existing_items.add(eid)

    added = 0
    for f in sorted(os.listdir(TRANSCRIPT_DIR)):
        if not f.endswith(".txt"):
            continue
        base = f.replace(".txt", "")
        if base in existing_items:
            continue
        # Check if already processed
        processed_log = load_json(LOG_FILE, [])
        already_done = False
        for entry in processed_log:
            if entry.get("video_id") == base and entry.get("status") == "SUCCESS":
                already_done = True
                break
        if already_done:
            existing_items.add(base)
            continue

        queue.append({
            "video_id": base,
            "title": base.replace("_", " ").replace("20260530 ", "").strip(),
            "transcript": f,
            "processed": False,
            "added": ts_iso()
        })
        existing_items.add(base)
        added += 1

    if added > 0:
        save_json(QUEUE_FILE, queue)
        log(f"Auto-seeded {added} new transcripts into queue")

    # Update queue count in status
    total = len(queue)
    pending = sum(1 for q in queue if not q.get("processed", False))
    update_status({"queue_total": total, "queue_pending": pending})
    log(f"Queue: {pending} pending, {total - pending} processed (of {total} total)")

def get_next_video():
    """Get next unprocessed video from queue."""
    queue = load_json(QUEUE_FILE, [])
    for item in queue:
        if item.get("processed", False):
            continue
        video_id = item.get("video_id") or item.get("id", "unknown")
        title = item.get("title") or video_id

        # Find the transcript file
        transcript_file = item.get("transcript", "")
        transcript_path = os.path.join(TRANSCRIPT_DIR, transcript_file)

        if not os.path.exists(transcript_path):
            # Try to find by partial match
            found = False
            for f in os.listdir(TRANSCRIPT_DIR):
                if f.endswith(".txt") and video_id[:25].replace(" ", "_") in f:
                    transcript_path = os.path.join(TRANSCRIPT_DIR, f)
                    found = True
                    break
            if not found:
                log(f"  Skipping {video_id}: transcript file not found")
                item["processed"] = True
                item["error"] = "Transcript not found"
                save_json(QUEUE_FILE, queue)
                continue

        return {
            "video_id": video_id,
            "title": title,
            "transcript_path": transcript_path,
            "queue_item": item
        }
    return None

# ── Codebase context loader ────────────────────────────────────────────────
CODEBASE_CONTEXT_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".codebase_context.txt")

def load_codebase_context():
    try:
        with open(CODEBASE_CONTEXT_FILE) as f:
            return f.read().strip()
    except (FileNotFoundError, OSError):
        log("WARNING: Codebase context file not found. Pipeline will run without codebase awareness.")
        return ""

# ── Tier-based pipeline stage prompts ──────────────────────────────────────

# Tier 1: Quick analysis (for shorts / very short transcripts)
TIER1_ANALYSIS_PROMPT = """You are an AI content analyst. This is a short transcript (Shorts or brief clip).
Produce a concise 2-3 paragraph analysis covering:
1. Main topic and key insight
2. Any specific tools, libraries, or patterns mentioned
3. Relevance to .NET AI/agent development (CodebrewRouter context)

Keep it tight and useful. No markdown formatting needed.

TRANSCRIPT:
{transcript}"""

# Tier 2: Medium analysis (for medium-length content)
TIER2_PLAN_PROMPT = """You are an AI content analyst for the CodebrewRouter project (Blaze.LlmGateway — .NET 10 MEAI LLM routing proxy). Analyze this transcript and produce a simple plan that connects the content to the actual codebase.

PROJECT CODEBASE:
{codebase_context}

OUTPUT FORMAT - Valid JSON only:
{{
  "metadata": {{
    "title": "string",
    "primary_themes": ["string"],
    "tech_stack": ["string"],
    "relevance": "HIGH|MEDIUM|LOW",
    "persona_fit": "string - why this matters to Allen (active .NET AI gateway builder)",
    "codebase_areas_affected": ["string - files/directories impacted"]
  }},
  "sections": [
    {{
      "heading": "string",
      "key_points": ["string"],
      "project_impact": "DIRECT|NEW|INFORM|NONE",
      "codebase_mapping": "string - which CodebrewRouter files/classes this maps to",
      "code_needed": true|false
    }}
  ],
  "implementation_roadmap": [
    {{
      "priority": "P0|P1|P2",
      "action": "string",
      "target_file": "string",
      "effort_minutes": "integer",
      "success_criteria": "string"
    }}
  ],
  "project_ideas": []
}}

TRANSCRIPT:
{transcript}"""

# Tier 3: Full plan (for long, high-quality content)
TIER3_PLAN_PROMPT = """You are the Lead Architect for a .NET AI Gateway project called CodebrewRouter (Blaze.LlmGateway). Your task is to analyze a cleaned video transcript, cross-reference it with the actual project codebase, and produce a rigid Technical Specification Manifest (JSON) that an LLM (Codex) will follow to generate the final analysis.

ROLE: You are evaluating this content FOR the CodebrewRouter project. Every insight must be mapped to real project concerns. Assume the reader is Allen (Petey) — a hands-on .NET architect who actively builds and ships this code.

CODEBREWROUTER CODEBASE (current state):
{codebase_context}

PROJECT CONTEXT:
- CodebrewRouter = Blaze.LlmGateway: .NET 10 MEAI LLM routing proxy at /mnt/data/src/CodebrewRouter
- Focus areas: provider routing, MCP tool integration, circuit breakers, observability, multi-model orchestration
- Squad: 9-agent development team (Conductor, Planner, Architect, Coder, Tester, Reviewer, Infra, Security-Review, Orchestrator)

STRICT RULES:
- Output ONLY valid JSON matching the schema below
- NO prose, NO commentary, NO markdown formatting around the JSON
- The "requirements" field must be specific, actionable items Codex can check off
- The "acceptance_criteria" field must be objectively verifiable (pass/fail)
- For each section, determine HOW the transcript content maps to CodebrewRouter code
- The "project_impact" field must rate if this insight directly affects existing code: "DIRECT" (change existing code), "NEW" (add new component), "INFORM" (strategy/design guidance), or "NONE" (not applicable)
- If the video is NOT relevant to CodebrewRouter (relevance = LOW), fill "project_ideas" with persona-fit alternatives

SCHEMA:
{{
  "metadata": {{
    "title": "string - extracted video title",
    "primary_themes": ["string - 2-4 main topics"],
    "tech_stack": ["string - specific tools/libraries mentioned"],
    "codebrew_relevance": "HIGH|MEDIUM|LOW",
    "persona_fit": "string - Why this matters to an active .NET AI gateway builder - Allen/Petey",
    "codebase_areas_affected": ["string - names of projects/files/directories impacted"]
  }},
  "files_to_generate": [
    {{
      "filename": "string - analysis.md",
      "sections": [
        {{
          "heading": "string - section title",
          "project_impact": "DIRECT|NEW|INFORM|NONE",
          "requirements": ["string - specific content requirements"],
          "key_points": ["string - essential points to include"],
          "acceptance_criteria": ["string - verifiable checks"],
          "codebase_mapping": "string - which specific CodebrewRouter files/classes/interfaces this maps to in the actual code",
          "code_stubs_needed": ["string - optional, only if code patterns are needed"]
        }}
      ]
    }}
  ],
  "implementation_roadmap": [
    {{
      "priority": "P0|P1|P2",
      "action": "string - specific implementation step",
      "target_file": "string - which code file this targets in CodebrewRouter",
      "effort_minutes": "integer - estimated effort",
      "dependencies": ["string - prerequisites"],
      "success_criteria": "string - how to verify it's done"
    }}
  ],
  "roadmap_items": [
    {{
      "feature": "string - feature name",
      "complexity": "Low|Medium|High",
      "implementation_details": "string - technical steps for Codex"
    }}
  ],
  "project_ideas": [
    {{
      "name": "string",
      "problem": "string - why this fits Allen's persona (.NET/MEAI/agents)",
      "tech_stack": ["string"],
      "mvp_timeline": "string",
      "relevance_to_gateway": "string - how this connects to CodebrewRouter concerns"
    }}
  ],
  "source_url": "string"
}}

CRITICAL INSTRUCTION: Before writing JSON, consider:
1. What in this transcript can we APPLY to CodebrewRouter code right now?
2. Which existing files/classes would change?
3. What's the persona-fit for Allen (Petey) as an active .NET AI gateway architect?
4. If the topic is unrelated, suggest a project idea that DOES fit his stack and interests

CLEANED TRANSCRIPT:
{transcript}"""

# Tier 3 Codex execution prompt
TIER3_CODEX_PROMPT = """You are a Senior Technical Writer and .NET Architect. You have been given a Technical Specification Manifest from the Lead Architect. Your job is to execute the plan exactly as specified and produce a complete analysis.md.

THE PLAN:
{plan}

EXECUTION RULES:
1. Generate each file listed in "files_to_generate" as a complete markdown document
2. Follow the "requirements" for each section precisely
3. Include all "key_points" in the appropriate sections
4. Include a "### 🎯 Project Impact" subsection in each section that explains:
   - How this insight affects CodebrewRouter code (from "project_impact" field)
   - Which actual files/classes are affected (from "codebase_mapping" field)
   - What would need to change or be added
5. Write code stubs where "code_stubs_needed" is specified — these are illustrative patterns, NOT production code to commit
6. The output must be clean, professional markdown suitable for a senior architect
7. If the plan says relevance is LOW, generate project_ideas with full detail instead of analysis
8. DO NOT add sections not in the plan
9. DO NOT skip any required section
10. The output MUST start with the filename as a markdown H2 header: ## filename: analysis.md
11. The analysis must be self-contained
12. At the END of the document, add a "## 📋 Implementation Roadmap" section (derived from the "implementation_roadmap" array in the plan). Format each item as:

### P0: [Action]
- **Target:** `[target_file]`
- **Effort:** [effort_minutes] min
- **Depends on:** [dependencies]
- **Verify:** [success_criteria]

13. At the very end, add "## 🧭 Next Steps & Recommendations" with:
   - 2-3 recommended next actions for Allen
   - What to prioritize
   - Any quick wins worth implementing immediately

EXTRA CONTEXT FROM TRANSCRIPT:
{transcript_excerpt}"""

# Tier 2 Codex execution prompt (simpler)
TIER2_CODEX_PROMPT = """You are a Technical Writer for the CodebrewRouter project. Given this analysis plan, produce a well-structured markdown document that connects insights to the actual codebase.

THE PLAN:
{plan}

OUTPUT A SINGLE FILE with these sections:
1. Overview - what this content is about and why it matters to CodebrewRouter
2. Key Technical Details - specific tools, patterns, code mentioned
3. Relevance to CodebrewRouter - how this applies to the .NET AI Gateway project
   - Include a "🎯 Project Impact" note per insight (what code/files change)
4. Action Items - specific things to investigate or implement
5. 📋 Implementation Roadmap (from the plan's implementation_roadmap field)
6. 🧭 Next Steps & Recommendations - 2-3 quick wins

Make it concise but complete. Markdown format with code blocks where appropriate."""

# Validation prompt
VALIDATION_PROMPT = """You are a Quality Assurance validator for AI-generated content. Compare the ORIGINAL PLAN against the EXECUTED OUTPUT and produce a validation report.

ORIGINAL PLAN:
{plan}

EXECUTED OUTPUT:
{output}

VALIDATION TASKS:
1. Check that all required sections from the plan appear in the output
2. Check for hallucinations - content not supported by the plan
3. Verify the "🎯 Project Impact" subsection exists for each major section
4. Verify the "📋 Implementation Roadmap" section exists and maps to the plan
5. Verify the "🧭 Next Steps & Recommendations" section exists at the end
6. Rate overall fidelity: PASS, PASS_WITH_MINOR_ISSUES, or FAIL

OUTPUT FORMAT - Valid JSON only:
{{
  "overall_rating": "PASS|PASS_WITH_MINOR_ISSUES|FAIL",
  "sections_present": ["list of headings found"],
  "issues": ["any specific issues"],
  "can_auto_retry": true|false
}}"""

# Save prompt
SAVE_PROMPT = """You are a document archivist. Generate the metadata needed to save the following analysis to Google Drive.

OUTPUT FORMAT - Valid JSON only:
{{
  "filename": "YYYYMMDD_Short_Descriptive_Title.md",
  "drive_title": "string - clean title for Drive display",
  "summary": "string - one sentence summary",
  "tags": ["tag1", "tag2"]
}}

CONTENT:
{output}

VIDEO TITLE: {title}"""

# Cleanup prompt (same for all tiers)
CLEANUP_PROMPT = """You are a transcript condensation specialist. Your job is to take a raw, verbose Whisper transcription and produce a dense, cleaned version that preserves ALL technical content while removing:

1. Filler words and phrases ("um", "uh", "like", "you know", "so basically", "right?")
2. Repeated false starts and self-corrections
3. Irrelevant social banter and sign-offs
4. Timestamp artifacts and speaker label debris
5. Redundant or near-identical restatements (keep the clearest version)
6. Audience questions that go unanswered or off-topic tangents
7. Promotional/marketing fluff for unrelated products

CRITICAL RULES:
- Preserve EVERY technical detail: API names, package names, version numbers, URLs, code snippets
- Preserve ALL architectural patterns, implementation steps, and configuration values
- Preserve exact quotes for key insights or claims
- Preserve chronological flow of the discussion
- DO NOT add, infer, or fabricate content not in the original
- DO NOT summarize away implementation details
- Aim to reduce verbosity by 50-70% while keeping 100% of the technical signal
- Output only the cleaned transcript, no commentary

RAW TRANSCRIPT:
{transcript}"""

# ── JSON parser helper ──────────────────────────────────────────────────────
def extract_json(text):
    if "```json" in text:
        text = text.split("```json")[1].split("```")[0].strip()
    elif "```" in text:
        blocks = text.split("```")
        for block in blocks:
            block = block.strip()
            if block.startswith("{") or block.startswith("["):
                text = block
                break
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return None

# ── Pipeline Orchestrator ────────────────────────────────────────────────────
def run_tier1(video, cleaned):
    """Tier 1: Quick analysis for shorts - Gemini only, no Codex, no Drive."""
    video_id = video["video_id"]
    title = video["title"]

    log("  Tier 1 mode: Gemini quick analysis (Shorts/short content)")
    analysis_prompt = TIER1_ANALYSIS_PROMPT.format(transcript=cleaned)
    analysis = call_gemini(analysis_prompt)

    if analysis.startswith("ERROR:"):
        return False, {"error": analysis}

    # Save locally
    out_dir = os.path.join(DATA_DIR, "outputs")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, f"{video_id}_tier1.md")
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(analysis)
    log(f"  Tier 1 output saved: {len(analysis):,} chars")

    # Log result
    append_log({
        "video_id": video_id, "title": title, "tier": 1, "status": "SUCCESS",
        "stages": {"cleanup": f"{len(cleaned):,} chars", "analysis": f"{len(analysis):,} chars"},
        "time": ts_iso()
    })
    return True, {"analysis": analysis, "output_path": out_path}

def run_tier2(video, cleaned):
    """Tier 2: Gemini plan + Codex execute + Gemini validate + Drive."""
    video_id = video["video_id"]
    title = video["title"]

    log("  Tier 2 mode: Medium content pipeline")

    # Stage 2A: Plan (Gemini equivalent) — with codebase awareness
    log("  Stage 2: Planning...")
    codebase_ctx = load_codebase_context()
    plan_prompt = TIER2_PLAN_PROMPT.format(codebase_context=codebase_ctx, transcript=cleaned)
    plan_raw = call_gemini(plan_prompt)

    if plan_raw.startswith("ERROR:"):
        return False, {"error": f"Plan failed: {plan_raw}"}

    plan = extract_json(plan_raw) or {
        "metadata": {"title": title, "primary_themes": [], "tech_stack": [], "relevance": "MEDIUM"},
        "sections": [{"heading": "Summary", "key_points": ["See transcript"], "code_needed": False}]
    }
    relevance = plan.get("metadata", {}).get("relevance", "MEDIUM")
    plan_str = json.dumps(plan, indent=2)
    log(f"  Plan done. Relevance: {relevance}")

    os.makedirs(os.path.join(DATA_DIR, "plans"), exist_ok=True)
    with open(os.path.join(DATA_DIR, "plans", f"{video_id}.json"), "w") as f:
        json.dump(plan, f, indent=2)

    # Stage 2B: Execute (Codex with model fallback)
    log("  Stage 3: Execution...")
    codex_prompt_tier2 = TIER2_CODEX_PROMPT.format(plan=plan_str)
    codex_output, codex_source = execute_plan_with_fallback(codex_prompt_tier2, title, plan_str, "tier2")
    log(f"  Execution source: {codex_source}, output: {len(codex_output):,} chars")

    os.makedirs(os.path.join(DATA_DIR, "outputs"), exist_ok=True)
    out_path = os.path.join(DATA_DIR, "outputs", f"{video_id}_tier2.md")
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(codex_output)

    # Stage 2C: Validate (Gemini)
    log("  Stage 4: Validation...")
    validate_prompt = VALIDATION_PROMPT.format(plan=plan_str, output=codex_output[:30000])
    validation_raw = call_gemini(validate_prompt)
    validation = extract_json(validation_raw) or {"overall_rating": "PASS", "issues": ["Parse failed"]}
    rating = validation.get("overall_rating", "PASS")
    log(f"  Validation: {rating}")

    # Stage 2D: Save to Drive
    drive_result = save_to_drive(video, codex_output, title, relevance != "LOW")

    append_log({
        "video_id": video_id, "title": title, "tier": 2, "status": "SUCCESS",
        "stages": {
            "cleanup": f"{len(cleaned):,} chars",
            "plan": f"relevance={relevance}",
            "codex": f"{len(codex_output):,} chars",
            "validation": rating,
            "drive": "ok" if drive_result else "failed"
        },
        "time": ts_iso()
    })
    return True, {"output": codex_output, "validation": rating, "drive": drive_result}

def run_tier3(video, cleaned):
    """Tier 3: Full pipeline with Azure Foundry planner + Codex + Validate + Drive."""
    video_id = video["video_id"]
    title = video["title"]

    log("  Tier 3 mode: Full pipeline")

    # Stage 2: Plan (Azure Foundry/fallback) — with codebase awareness
    log("  Stage 2: Planning (Azure Foundry / fallback)...")
    codebase_ctx = load_codebase_context()
    codebase_section = f"\n\nCODEBASE CONTEXT:\n{codebase_ctx}\n\n" if codebase_ctx else ""
    plan_prompt = TIER3_PLAN_PROMPT.format(codebase_context=codebase_ctx, transcript=cleaned)
    plan_raw = call_planner(plan_prompt)

    if plan_raw.startswith("ERROR:"):
        return False, {"error": plan_raw}

    plan = extract_json(plan_raw)
    if plan is None:
        log("  Could not parse plan JSON, using raw fallback")
        plan = {
            "metadata": {"title": title, "primary_themes": [], "tech_stack": [], "codebrew_relevance": "MEDIUM"},
            "files_to_generate": [], "roadmap_items": []
        }
        plan_json_str = plan_raw
    else:
        plan_json_str = json.dumps(plan, indent=2)

    useful = plan.get("metadata", {}).get("codebrew_relevance", "MEDIUM") != "NONE"
    relevance = plan.get("metadata", {}).get("codebrew_relevance", "UNKNOWN")
    log(f"  Plan done. Relevance: {relevance}")

    os.makedirs(os.path.join(DATA_DIR, "plans"), exist_ok=True)
    with open(os.path.join(DATA_DIR, "plans", f"{video_id}.json"), "w") as f:
        json.dump(plan, f, indent=2)

    # Stage 3: Execute (Codex with model fallback)
    log("  Stage 3: Execution...")
    if len(cleaned) > 8000:
        excerpt = cleaned[:4000] + "\n\n[...content omitted...]\n\n" + cleaned[-4000:]
    else:
        excerpt = cleaned

    codex_prompt = TIER3_CODEX_PROMPT.format(plan=plan_json_str, transcript_excerpt=excerpt)
    codex_output, codex_source = execute_plan_with_fallback(codex_prompt, title, plan_json_str, "tier3")
    log(f"  Execution source: {codex_source}, output: {len(codex_output):,} chars")

    os.makedirs(os.path.join(DATA_DIR, "outputs"), exist_ok=True)
    out_path = os.path.join(DATA_DIR, "outputs", f"{video_id}_tier3.md")
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(codex_output)

    # Stage 4: Validate
    log("  Stage 4: Validation (Gemini/fallback)...")
    validate_prompt = VALIDATION_PROMPT.format(plan=plan_json_str, output=codex_output[:30000])
    validation_raw = call_gemini(validate_prompt)
    validation = extract_json(validation_raw) or {"overall_rating": "PASS", "issues": ["Parse failed"]}
    rating = validation.get("overall_rating", "FAIL")
    log(f"  Validation: {rating}")

    if rating == "FAIL" and validation.get("can_auto_retry", False):
        log("  Auto-retrying Codex with fix instructions...")
        fix_instructions = validation.get("issues", [])
        retry_prompt = codex_prompt + "\n\nFIX INSTRUCTIONS FROM VALIDATOR:\n" + "\n".join(fix_instructions[:3])
        codex_output = call_codex(retry_prompt)
        log(f"  Retry output: {len(codex_output):,} chars")
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(codex_output)

    # Stage 5: Save to Drive
    drive_result = save_to_drive(video, codex_output, title, useful)

    append_log({
        "video_id": video_id, "title": title, "tier": 3, "status": "SUCCESS",
        "stages": {
            "cleanup": f"{len(cleaned):,} chars",
            "plan": f"relevance={relevance}",
            "codex": f"{len(codex_output):,} chars",
            "validation": rating,
            "drive": "ok" if drive_result else "failed"
        },
        "time": ts_iso()
    })
    return True, {"output": codex_output, "validation": rating, "drive": drive_result}

def save_to_drive(video, content, title, useful):
    """Upload output to Google Drive. Returns True on success."""
    video_id = video["video_id"]
    log("  Saving to Google Drive...")
    try:
        token = get_token()
        with open(CODEBREW_FILE) as f:
            cf = json.load(f)
        root_folder_id = cf["folder_id"]

        date_str = datetime.now().strftime("%Y-%m-%d")
        date_folder_id = find_or_create_folder(token, root_folder_id, date_str)
        log(f"  Drive date folder {date_str}: id={date_folder_id}")

        # Generate filename metadata
        save_prompt = SAVE_PROMPT.format(output=content[:5000], title=title)
        save_meta_raw = call_gemini(save_prompt)
        parsed_meta = extract_json(save_meta_raw)
        filename = f"{video_id}.md"
        if parsed_meta and "filename" in parsed_meta:
            filename = parsed_meta["filename"]

        file_id = upload_to_drive(token, date_folder_id, filename, content)
        log(f"  Uploaded: {filename} (id: {file_id})")

        drive_info = {"file_id": file_id, "date_folder": date_str, "filename": filename}
        os.makedirs(os.path.join(DATA_DIR, "drive_files"), exist_ok=True)
        with open(os.path.join(DATA_DIR, "drive_files", f"{video_id}.json"), "w") as f:
            json.dump(drive_info, f, indent=2)
        return True
    except Exception as e:
        log(f"  Drive upload failed: {e}")
        log("  (Analysis saved locally)")
        return False

def mark_processed(video):
    """Mark a video as processed in the queue."""
    if video.get("queue_item") is not None:
        queue = load_json(QUEUE_FILE, [])
        video_id = video["video_id"]
        for q in queue:
            qid = q.get("video_id") or q.get("id", "")
            if qid == video_id:
                q["processed"] = True
                q["date_processed"] = ts_iso()
                q["pipeline_version"] = "v3"
                break
        save_json(QUEUE_FILE, queue)

# ── Git pull + codebase refresh ──────────────────────────────────────────────
def git_pull_all_repos():
    """Pull latest develop from all git repos under /mnt/data/src/."""
    repos_base = "/mnt/data/src"
    if not os.path.isdir(repos_base):
        log(f"WARNING: {repos_base} does not exist, skipping git pull")
        return
    for item in sorted(os.listdir(repos_base)):
        repo_path = os.path.join(repos_base, item)
        if not os.path.isdir(os.path.join(repo_path, ".git")):
            continue
        log(f"Git pull: {item}...")
        try:
            subprocess.run(
                ["git", "fetch", "origin", "--prune"],
                capture_output=True, text=True, timeout=30, cwd=repo_path
            )
            for branch in ["develop", "main", "master"]:
                r = subprocess.run(
                    ["git", "show-ref", "--verify", f"refs/heads/{branch}"],
                    capture_output=True, timeout=10, cwd=repo_path
                )
                if r.returncode != 0:
                    continue
                subprocess.run(
                    ["git", "checkout", branch],
                    capture_output=True, timeout=30, cwd=repo_path
                )
                r2 = subprocess.run(
                    ["git", "merge", "--ff-only", f"origin/{branch}"],
                    capture_output=True, text=True, timeout=30, cwd=repo_path
                )
                if r2.returncode == 0:
                    log(f"  {item}: pulled latest {branch}")
                else:
                    log(f"  {item}: fetched but local changes prevent merge ({r2.stderr.strip()[:100]})")
                break
        except subprocess.TimeoutExpired:
            log(f"  git operation timed out for {item}")
        except Exception as e:
            log(f"  git error for {item}: {e}")

def refresh_codebase_context():
    """Run scan_codebase.py to regenerate the codebase context snapshot from latest code."""
    scan_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "scan_codebase.py")
    if not os.path.exists(scan_path):
        log("WARNING: scan_codebase.py not found alongside pipeline script")
        return
    try:
        r = subprocess.run(
            ["python3", scan_path],
            capture_output=True, text=True, timeout=60
        )
        if r.returncode == 0:
            log(r.stdout.strip())
        else:
            log(f"WARNING: Codebase scan failed: {r.stderr.strip()[:200]}")
    except subprocess.TimeoutExpired:
        log("WARNING: Codebase scan timed out")
    except Exception as e:
        log(f"WARNING: Codebase scan error: {e}")

# ── Main ─────────────────────────────────────────────────────────────────────
def main():
    result = {
        "status": "UNKNOWN",
        "video": None,
        "tier": None,
        "stages": {},
        "error": None,
        "duration_seconds": 0,
        "completed_at": None
    }
    start_time = time.time()

    log("=" * 60)
    log("YouTube AI 5-Stage Pipeline v3")
    log(f"Started: {ts_now()}")
    log("=" * 60)

    # Step 0: Git pull latest from all repos under /mnt/data/src/
    git_pull_all_repos()

    # Step 0a: Refresh codebase context from latest code
    refresh_codebase_context()

    # Step 0b: Auto-seed queue from transcripts directory
    auto_seed_queue()

    # Pre-flight model health check
    gemini_ok, planner_ok, codex_ok, executor_ok = test_models()

    if not gemini_ok:
        log("GUARDRAIL: No Gemini/cleanup model available. Aborting.")
        update_status({"health": {"gemini": False, "planner": planner_ok, "codex": codex_ok, "executor": executor_ok}})
        result["status"] = "FAILED"
        result["error"] = "No Gemini model available"
        print(json.dumps(result))
        sys.exit(1)

    if not codex_ok:
        if executor_ok:
            log("WARNING: Codex not available but executor fallback models are. Will use model-based execution for Tier 2/3.")
        else:
            log("WARNING: Codex not available and no executor fallback. Will skip Codex stages and use plan dumps for Tier 2/3.")

    update_status({"health": {"gemini": gemini_ok, "planner": planner_ok, "codex": codex_ok, "executor": executor_ok}})

    # Find next video
    video = get_next_video()
    if video is None:
        log("Nothing to process. Exiting.")
        result["status"] = "IDLE"
        result["error"] = "No pending videos in queue"
        update_status({"last_result": "IDLE", "queue_pending": 0})
        print(json.dumps(result))
        return

    log(f"Selected: {video['title']}")
    result["video"] = video["video_id"]
    result["title"] = video["title"]

    # Read transcript
    transcript_path = video["transcript_path"]
    if not os.path.exists(transcript_path):
        log(f"ERROR: Transcript not found: {transcript_path}")
        append_log({"video_id": video["video_id"], "title": video["title"], "status": "FAILED", "stage": "read", "error": "File not found"})
        result["status"] = "FAILED"
        result["error"] = "Transcript file not found"
        print(json.dumps(result))
        sys.exit(1)

    with open(transcript_path, encoding="utf-8", errors="replace") as f:
        raw_transcript = f.read()

    # Determine tier
    tier, tier_name = detect_tier(raw_transcript)
    log(f"Complexity tier: {tier} ({tier_name}) - {len(raw_transcript):,} chars")
    result["tier"] = tier

    # Stage 1: Cleanup (always needed)
    log("Stage 1: Cleanup (Gemini/fallback)...")
    cleanup_prompt = CLEANUP_PROMPT.format(transcript=raw_transcript)
    cleaned = call_gemini(cleanup_prompt)

    if cleaned.startswith("ERROR:"):
        log(f"Stage 1 FAILED: {cleaned}")
        append_log({"video_id": video["video_id"], "title": video["title"], "tier": tier, "status": "FAILED", "stage": "cleanup", "error": cleaned})
        result["status"] = "FAILED"
        result["error"] = cleaned
        print(json.dumps(result))
        sys.exit(1)

    reduction = len(cleaned) * 100 // max(len(raw_transcript), 1)
    log(f"Stage 1 done: {len(cleaned):,} chars (was {len(raw_transcript):,}, {reduction}%)")
    result["stages"]["cleanup"] = {"chars_before": len(raw_transcript), "chars_after": len(cleaned)}

    os.makedirs(os.path.join(DATA_DIR, "cleaned"), exist_ok=True)
    with open(os.path.join(DATA_DIR, "cleaned", f"{video['video_id']}.txt"), "w", encoding="utf-8") as f:
        f.write(cleaned)

    # Run tier-specific pipeline
    success = False
    result_data = {}

    if tier == 1:
        log(f"Running Tier 1 pipeline (Shorts)...")
        success, result_data = run_tier1(video, cleaned)
    elif tier == 2:
        log(f"Running Tier 2 pipeline (Medium)...")
        success, result_data = run_tier2(video, cleaned)
    else:
        log(f"Running Tier 3 pipeline (Full)...")
        success, result_data = run_tier3(video, cleaned)

    if success:
        mark_processed(video)
        result["status"] = "SUCCESS"
        result["stages"].update(result_data)
        log(f"Pipeline completed successfully: {video['title']}")
    else:
        error_msg = result_data.get("error", "Unknown error")
        log(f"Pipeline failed: {error_msg}")
        append_log({"video_id": video["video_id"], "title": video["title"], "tier": tier, "status": "FAILED", "error": error_msg})
        result["status"] = "FAILED"
        result["error"] = error_msg

    result["duration_seconds"] = int(time.time() - start_time)
    result["completed_at"] = ts_iso()
    update_status({
        "last_result": result["status"],
        "last_video": video["video_id"],
        "last_title": video["title"],
        "last_tier": tier,
        "last_duration": result["duration_seconds"],
        "last_run": ts_iso()
    })

    # Output JSON summary for cron delivery
    print("\n---PIPELINE_RESULT---")
    print(json.dumps(result, indent=2))
    print("---END_PIPELINE_RESULT---")

if __name__ == "__main__":
    main()

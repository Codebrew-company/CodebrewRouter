#!/usr/bin/env python3
"""
AI Code Review — runs inside GitHub Actions on the self-hosted runner.
Reads the PR diff via git, sends it to Azure Foundry for review,
outputs a structured review body and event flag for gh pr review.
"""

import json
import os
import subprocess
import sys
import urllib.request

# ── Config ──────────────────────────────────────────────────────────────────
AZURE_ENDPOINT = os.environ.get(
    "AZURE_FOUNDRY_ENDPOINT",
    "https://codebrew-resource.openai.azure.com/openai/deployments/DeepSeek-V4-Pro/chat/completions?api-version=2024-10-21",
)
AZURE_KEY = os.environ.get("AZURE_FOUNDRY_KEY", "")
# To switch to gpt-5.4, set AZURE_DEPLOYMENT=gpt-5.4 in env
AZURE_DEPLOYMENT = os.environ.get("AZURE_DEPLOYMENT", "DeepSeek-V4-Pro")

PR_NUMBER = os.environ.get("PR_NUMBER", "")
BASE_REF = os.environ.get("BASE_REF", "develop")
HEAD_SHA = os.environ.get("HEAD_SHA", "")

OUTPUT_BODY = "/tmp/review_body.md"
OUTPUT_EVENT = "/tmp/review_event.txt"


def run(cmd, **kwargs):
    """Run a shell command and return (stdout, stderr, returncode)."""
    result = subprocess.run(
        cmd, capture_output=True, text=True, timeout=120, **kwargs
    )
    return result.stdout.strip(), result.stderr.strip(), result.returncode


def get_pr_context():
    """Gather PR metadata and diff."""
    ctx = {}

    # Diff against base branch
    out, _, _ = run(["git", "diff", f"origin/{BASE_REF}...HEAD", "--"])
    ctx["diff"] = out

    # File list with stats
    out, _, _ = run(
        ["git", "diff", f"origin/{BASE_REF}...HEAD", "--stat"]
    )
    ctx["files"] = out

    # Changed file names
    out, _, _ = run(
        ["git", "diff", f"origin/{BASE_REF}...HEAD", "--name-only"]
    )
    ctx["changed_files"] = out.splitlines() if out else []

    # PR metadata from env
    ctx["pr_number"] = PR_NUMBER
    ctx["base_ref"] = BASE_REF
    ctx["head_sha"] = HEAD_SHA

    return ctx


def build_prompt(ctx):
    """Build the system + user prompt for code review."""
    diff = ctx["diff"]
    files = ctx["files"]
    changed = ctx["changed_files"]

    # Truncate extremely large diffs to avoid token limits
    max_diff_chars = 80_000
    if len(diff) > max_diff_chars:
        diff = diff[:max_diff_chars] + f"\n\n... [truncated: diff was {len(diff)} chars]"

    file_list = "\n".join(f"- `{f}`" for f in changed) if changed else "(none)"

    system_prompt = """You are an expert senior software architect performing a code review. You are thorough, precise, and constructive.

Return your review as **valid JSON only** — no markdown wrapping, no explanation outside the JSON. Use this exact schema:

{
  "verdict": "APPROVE" | "REQUEST_CHANGES" | "COMMENT",
  "summary": "One-paragraph overview of the review findings",
  "inline_comments": [
    {
      "path": "relative/file/path.cs",
      "line": 42,
      "side": "RIGHT",
      "body": "**Severity:** issue description and suggestion"
    }
  ]
}

Review guidelines:
- **Critical** (security, data loss, correctness bugs) → REQUEST_CHANGES
- **Warnings** (code smell, missing edge cases, performance) → REQUEST_CHANGES or COMMENT
- **Suggestions** (style, minor improvements) → COMMENT
- **All clear** → APPROVE
- Inline comments should reference the exact file path and line number from the diff
- For `side`, use "RIGHT" for additions, "LEFT" for deletions (default: "RIGHT")
- Be specific — reference exact line numbers and suggest concrete fixes
- Check for: security vulnerabilities, race conditions, error handling, null safety, async/await correctness, disposal patterns, logging, and test coverage
- This is a .NET/C# project — follow .NET best practices"""

    user_prompt = f"""## Pull Request Review

### Changed Files
{file_list}

### Full Diff Stats
```
{files}
```

### Diff
```diff
{diff}
```

Review the above changes and return JSON."""
    return system_prompt, user_prompt


def call_llm(system_prompt, user_prompt):
    """Call Azure Foundry OpenAI-compatible endpoint."""
    body = json.dumps({
        "model": AZURE_DEPLOYMENT,
        "messages": [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt},
        ],
        "temperature": 0.1,
        "max_tokens": 4096,
    }).encode("utf-8")

    req = urllib.request.Request(
        AZURE_ENDPOINT,
        data=body,
        headers={
            "api-key": AZURE_KEY,
            "Content-Type": "application/json",
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            content = data["choices"][0]["message"]["content"]
            return content
    except Exception as e:
        print(f"::error::LLM call failed: {e}", file=sys.stderr)
        return json.dumps({
            "verdict": "COMMENT",
            "summary": f"AI review failed (LLM error: {e}). Manual review required.",
            "inline_comments": [],
        })


def parse_review(raw):
    """Extract JSON from the LLM response (handles markdown fences)."""
    # Strip markdown code fences if present
    text = raw.strip()
    if text.startswith("```"):
        # Find the first and last ```
        lines = text.splitlines()
        start = 0
        end = len(lines)
        for i, line in enumerate(lines):
            if line.startswith("```"):
                start = i + 1
                break
        for i in range(len(lines) - 1, -1, -1):
            if lines[i].startswith("```"):
                end = i
                break
        text = "\n".join(lines[start:end]).strip()

    try:
        return json.loads(text)
    except json.JSONDecodeError as e:
        print(f"::warning::Failed to parse review JSON: {e}", file=sys.stderr)
        print(f"::warning::Raw response: {text[:500]}", file=sys.stderr)
        return {
            "verdict": "COMMENT",
            "summary": "AI review generated but couldn't be parsed. Manual review recommended.",
            "inline_comments": [],
        }


def post_review(review, ctx):
    """Write review body and event file for gh pr review."""
    verdict = review.get("verdict", "COMMENT")
    summary = review.get("summary", "")
    comments = review.get("inline_comments", [])

    # Build the review body
    lines = []
    lines.append("## 🤖 AI Code Review\n")
    lines.append(f"**Verdict: {verdict}**\n")
    lines.append(f"{summary}\n")

    if comments:
        lines.append("---\n")
        lines.append("### Inline Comments\n")
        for c in comments:
            path = c.get("path", "?")
            line = c.get("line", "?")
            body = c.get("body", "")
            lines.append(f"**`{path}:{line}`**\n")
            lines.append(f"{body}\n")

    # Footer
    lines.append(
        f"\n---\n*Reviewed by AI via Azure Foundry (`{AZURE_DEPLOYMENT}`)*"
    )

    body = "\n".join(lines)

    # Write files for the workflow action
    with open(OUTPUT_BODY, "w") as f:
        f.write(body)

    # Map verdict to gh event flag
    event_map = {"APPROVE": "--approve", "REQUEST_CHANGES": "--request-changes"}
    event = event_map.get(verdict, "--comment")
    with open(OUTPUT_EVENT, "w") as f:
        f.write(event)

    print(f"Review verdict: {verdict}")
    print(f"Review body written to {OUTPUT_BODY}")
    print(f"Event flag: {event}")


def main():
    print("::group::Gathering PR context")
    ctx = get_pr_context()
    print(f"PR #{ctx['pr_number']} — {len(ctx['changed_files'])} files changed")
    print(f"Diff size: {len(ctx['diff'])} chars")
    print("::endgroup::")

    if not ctx["diff"]:
        print("No diff changes — skipping review.")
        with open(OUTPUT_EVENT, "w") as f:
            f.write("--comment")
        with open(OUTPUT_BODY, "w") as f:
            f.write("No changes to review.")
        return

    print("::group::Building review prompt")
    sys_prompt, user_prompt = build_prompt(ctx)
    print(f"System prompt: {len(sys_prompt)} chars")
    print(f"User prompt: {len(user_prompt)} chars")
    print("::endgroup::")

    print("::group::Calling LLM")
    raw = call_llm(sys_prompt, user_prompt)
    print(f"LLM response: {len(raw)} chars")
    print("::endgroup::")

    print("::group::Parsing review")
    review = parse_review(raw)
    print(f"Verdict: {review.get('verdict', '?')}")
    print(f"Comments: {len(review.get('inline_comments', []))}")
    print("::endgroup::")

    print("::group::Writing output")
    post_review(review, ctx)
    print("::endgroup::")


if __name__ == "__main__":
    main()

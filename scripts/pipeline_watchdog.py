#!/usr/bin/env python3
"""
Pipeline Watchdog — silent when healthy, alerts on failure.
Runs as a no_agent=True cron job every 60 minutes, delivers to Telegram.

Reads pipeline_status.json and checks:
  - File missing / unparseable
  - Last run stale (>27h old — pipeline runs daily at 08:00 UTC)
  - Last result was FAILED
  - Model health degraded (Gemini offline)

Empty stdout = silent tick (no delivery to user).
Non-empty stdout = alert delivered to Telegram verbatim.
Exit code != 0 = alert sent automatically by cron scheduler.
"""

import json, os, sys
from datetime import datetime, timezone

STATUS_FILE = os.path.expanduser("~/.hermes/data/youtube/pipeline_status.json")
MAX_AGE_HOURS = 27  # pipeline runs daily at 08:00 UTC; 27h = 3h buffer

def age_hours(ts_iso_str):
    """Compute age in hours of an ISO timestamp."""
    if not ts_iso_str:
        return None
    try:
        dt = datetime.fromisoformat(ts_iso_str)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        delta = datetime.now(timezone.utc) - dt
        return delta.total_seconds() / 3600
    except (ValueError, TypeError):
        return None

def main():
    if not os.path.exists(STATUS_FILE):
        print("ALERT: Pipeline status file not found.")
        print(f"Expected at: {STATUS_FILE}")
        print("The pipeline may have never run or the file was deleted.")
        sys.exit(1)

    try:
        with open(STATUS_FILE) as f:
            status = json.load(f)
    except (json.JSONDecodeError, OSError) as e:
        print(f"ALERT: Cannot read pipeline status file: {e}")
        sys.exit(1)

    alerts = []

    # Check model health
    health = status.get("health", {})
    if health.get("gemini") is False:
        alerts.append("Gemini model is DOWN — pipeline cannot run cleanup stage.")
    if health.get("codex") is False:
        alerts.append("Codex is DOWN — pipeline will skip Codex stages (reduced capability).")

    # Check last run freshness
    last_run = status.get("last_run")
    if not last_run:
        alerts.append("Pipeline has never completed a run (last_run is empty).")
    else:
        h = age_hours(last_run)
        if h is None:
            alerts.append(f"Cannot parse last_run timestamp: {last_run}")
        elif h > MAX_AGE_HOURS:
            alerts.append(f"Pipeline has not run in {h:.1f}h (max allowed: {MAX_AGE_HOURS}h).")
            alerts.append(f"Last run was: {last_run}")
            alerts.append("Check cron scheduler — the daily 08:00 UTC job may have stalled.")

    # Check last result
    last_result = status.get("last_result")
    if last_result == "FAILED":
        video = status.get("last_title", status.get("last_video", "unknown"))
        tier = status.get("last_tier", "?")
        duration = status.get("last_duration", "?")
        alerts.append(f"Pipeline FAILED on: {video} (tier {tier}, ran {duration}s)")

    # Check queue health
    pending = status.get("queue_pending", 0)
    total = status.get("queue_total", 0)
    if pending > 0 and last_result not in ("FAILED", None):
        # Only warn if there are pending items AND the last run wasn't already flagged
        # This is informational, not an error
        pass  # Don't alert on queue backlog unless it's extreme
    if total > 200:
        alerts.append(f"Queue is growing ({pending}/{total} pending) — may need manual review.")

    if alerts:
        print("YouTube AI Pipeline Alert")
        print("=" * 40)
        for a in alerts:
            print(f"  ! {a}")
        print()
        print(f"Status file: {STATUS_FILE}")
        print(f"Last updated: {status.get('last_updated', 'unknown')}")
        # Include the raw status for context
        print()
        print(json.dumps(status, indent=2))
        sys.exit(1)  # non-zero exit ensures cron scheduler flags it too
    else:
        # Silent — empty stdout means no delivery
        sys.exit(0)

if __name__ == "__main__":
    main()

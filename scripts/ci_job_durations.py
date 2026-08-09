#!/usr/bin/env python3
"""Reports how long recent CI jobs took, for investigating self-hosted-runner
performance (this repo's testflight.yml is dormant so far, but already
targets this Mac's own runner - see the workflow file's own comments).

Wraps `gh run list` / `gh api .../jobs` rather than talking to GitHub's REST
API directly, so it reuses the same auth already used interactively.

Usage:
    python3 scripts/ci_job_durations.py [--workflow testflight.yml] [--limit 10]

Each row is one job (not one run): a single run can have several jobs, and
each can queue independently on the self-hosted runner if this Mac is also
running jobs for another repo at the same time - the queue wait is part of
what makes a check slow, not just the build itself. See
~/AIFiles/docs/testflight-release.md "self-hosted runner" section for the
shared knowledge this pattern comes from (originally built for AppFactory,
Issue #664).
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone


def _gh_json(args: list[str]) -> object:
    result = subprocess.run(
        ["gh", *args], capture_output=True, text=True, check=True
    )
    return json.loads(result.stdout)


def _parse(ts: str | None) -> datetime | None:
    if not ts:
        return None
    return datetime.fromisoformat(ts.replace("Z", "+00:00"))


def _fmt_duration(start: datetime | None, end: datetime | None) -> str:
    if start is None or end is None:
        return "-"
    seconds = int((end - start).total_seconds())
    if seconds < 0:
        # Skipped/cancelled jobs report a completed_at that isn't
        # meaningfully "after" created_at/started_at.
        return "-"
    minutes, seconds = divmod(seconds, 60)
    return f"{minutes}m{seconds:02d}s"


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--workflow",
        default=None,
        help="Workflow file name (e.g. testflight.yml). Default: all workflows.",
    )
    parser.add_argument(
        "--limit", type=int, default=10, help="Number of runs to inspect (default: 10)"
    )
    args = parser.parse_args(argv[1:])

    run_list_args = [
        "run",
        "list",
        "--limit",
        str(args.limit),
        "--json",
        "databaseId,displayTitle,workflowName,conclusion,createdAt,event",
    ]
    if args.workflow:
        run_list_args += ["--workflow", args.workflow]

    runs = _gh_json(run_list_args)
    if not runs:
        print("No runs found.", file=sys.stderr)
        return 1

    header = (
        f"{'run':>10}  {'workflow':<12} {'event':<8} {'job':<16} "
        f"{'runner':<20} {'queued':>8} {'ran':>8} {'result':<10} title"
    )
    print(header)
    print("-" * len(header))

    for run in runs:
        run_id = run["databaseId"]
        jobs = _gh_json(
            ["api", f"repos/{{owner}}/{{repo}}/actions/runs/{run_id}/jobs"]
        )
        for job in jobs.get("jobs", []):
            queued_at = _parse(job.get("created_at"))
            started_at = _parse(job.get("started_at"))
            completed_at = _parse(job.get("completed_at"))
            runner = job.get("runner_name") or "-"
            print(
                f"{run_id:>10}  {run['workflowName']:<12.12} "
                f"{run['event']:<8.8} {job['name']:<16.16} "
                f"{runner:<20.20} "
                f"{_fmt_duration(queued_at, started_at):>8} "
                f"{_fmt_duration(started_at, completed_at):>8} "
                f"{(job.get('conclusion') or job['status']):<10.10} "
                f"{run['displayTitle'][:50]}"
            )

    print(
        "\n'queued' is time waiting for a runner to pick the job up - if this "
        "Mac is running another repo's job at the same time, this is that "
        "queueing cost. 'ran' is the job's own execution time.",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

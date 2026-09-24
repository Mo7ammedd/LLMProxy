#!/usr/bin/env python3
"""Block release images with fixable HIGH/CRITICAL findings; retain the full report."""
import json
import sys
from pathlib import Path


def main(paths):
    blocked = []
    for path in paths:
        report = json.loads(Path(path).read_text())
        if report.get("SchemaVersion") != 2 or "Metadata" not in report or "Results" not in report:
            raise ValueError(f"Invalid or incomplete Trivy report: {path}")
        for result in report["Results"]:
            for finding in result.get("Vulnerabilities") or []:
                if finding["Severity"] in {"HIGH", "CRITICAL"} and finding.get("FixedVersion"):
                    blocked.append(f"{finding['VulnerabilityID']}: {finding['PkgName']} "
                                   f"{finding['InstalledVersion']} -> {finding['FixedVersion']}")
    if blocked:
        print("Release blocked by fixable HIGH/CRITICAL vulnerabilities:\n" + "\n".join(sorted(set(blocked))))
        return 1
    print("No fixable HIGH/CRITICAL vulnerabilities found. Full reports retain all severities and unfixed findings.")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("Usage: check_vulnerabilities.py scan.json [scan.json ...]")
    raise SystemExit(main(sys.argv[1:]))

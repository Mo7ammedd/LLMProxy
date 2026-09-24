#!/usr/bin/env bash
set -euo pipefail

: "${IMAGE_DIGEST:?}" "${GHCR_IMAGE:?}" "${DOCKERHUB_IMAGE:?}" "${GITHUB_REPOSITORY:?}" "${GITHUB_REF:?}" "${GITHUB_SHA:?}"
mkdir -p artifacts/security
for architecture in amd64 arm64; do
  trivy image --platform "linux/$architecture" --scanners vuln --format json --timeout 10m \
    --output "artifacts/security/scan-$architecture.json" "$GHCR_IMAGE@$IMAGE_DIGEST"
done
python3 scripts/check_vulnerabilities.py artifacts/security/scan-amd64.json artifacts/security/scan-arm64.json
docker buildx imagetools inspect "$GHCR_IMAGE@$IMAGE_DIGEST" --format '{{json .SBOM}}' > artifacts/security/buildx-sbom.json
docker buildx imagetools inspect "$GHCR_IMAGE@$IMAGE_DIGEST" --format '{{json .Provenance}}' > artifacts/security/provenance.json
python3 - <<'PY'
import json, os
from pathlib import Path
directory = Path('artifacts/security')
sboms = json.loads((directory / 'buildx-sbom.json').read_text())
provenance = json.loads((directory / 'provenance.json').read_text())
reports = {}
for architecture in ('amd64', 'arm64'):
    platform = f'linux/{architecture}'
    sbom = sboms[platform]['SPDX']
    assert sbom['spdxVersion'].startswith('SPDX-'), f'Missing SPDX SBOM for {platform}'
    assert provenance[platform], f'Missing provenance for {platform}'
    (directory / f'sbom-{architecture}.spdx.json').write_text(json.dumps(sbom, indent=2) + '\n')
    reports[platform] = json.loads((directory / f'scan-{architecture}.json').read_text())
predicate = {
    'scanner': 'Trivy v0.74.0', 'source_commit': os.environ['GITHUB_SHA'],
    'image_digest': os.environ['IMAGE_DIGEST'],
    'policy': 'No fixable HIGH or CRITICAL vulnerabilities; full reports include unfixed findings.',
    'reports': reports,
}
(directory / 'scan-attestation.json').write_text(json.dumps(predicate, indent=2) + '\n')
(directory / 'image-digest.txt').write_text(os.environ['IMAGE_DIGEST'] + '\n')
PY

identity="https://github.com/$GITHUB_REPOSITORY/.github/workflows/ci.yml@$GITHUB_REF"
issuer="https://token.actions.githubusercontent.com"
predicate_type="https://github.com/Mo7ammedd/LLMProxy/attestations/trivy/v1"
for registry in ghcr dockerhub; do
  if [[ "$registry" == ghcr ]]; then image_name="$GHCR_IMAGE"; else image_name="$DOCKERHUB_IMAGE"; fi
  cosign sign --yes "$image_name@$IMAGE_DIGEST"
  cosign attest --yes --type "$predicate_type" --predicate artifacts/security/scan-attestation.json "$image_name@$IMAGE_DIGEST"
  cosign verify --certificate-identity "$identity" --certificate-oidc-issuer "$issuer" \
    "$image_name@$IMAGE_DIGEST" > "artifacts/security/signature-$registry.json"
  cosign verify-attestation --type "$predicate_type" --certificate-identity "$identity" --certificate-oidc-issuer "$issuer" \
    "$image_name@$IMAGE_DIGEST" > "artifacts/security/attestation-$registry.json"
done
python3 - <<'PY'
import hashlib
from pathlib import Path
directory = Path('artifacts/security')
files = sorted(p for p in directory.iterdir() if p.is_file() and p.name != 'SHA256SUMS')
(directory / 'SHA256SUMS').write_text(''.join(f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n' for p in files))
PY
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  cat >> "$GITHUB_STEP_SUMMARY" <<'EOF'

Both image digests passed signature and scan-attestation verification. Full vulnerability reports, SPDX SBOMs and build provenance are available in the security-release artifact.
EOF
fi

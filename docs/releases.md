# Images and releases

## Current release

LLMProxy 0.3.0 supports `linux/amd64` and `linux/arm64`. Docker selects the platform from the multi-platform image index.

```bash
docker pull mohammedtv/llmproxy:0.3.0
```

For immutable deployments, use the digest recorded in the [v0.3.0 release](https://github.com/Mo7ammedd/LLMProxy/releases/tag/v0.3.0), then verify its signature as described below. Use that same image reference in `docker run` or as `LLMPROXY_IMAGE` in Compose. The index digest is identical in Docker Hub and GHCR. Read the [upgrade notes](../CHANGELOG.md#upgrading-from-020) before replacing an existing deployment.

The original v0.2.0 source tag remains `1911fc881e39bbe033a7fa57a4d7ce9573a05c6c`. Its Docker Hub digest remains `sha256:a8bd1b3e93b12549733b86fbb0be9809704924e2a2e27bee075df4c7ab56d3db`; its GHCR tag build has separate metadata/digest. Version 0.2 did not include the v0.3 signing pipeline. Historical version tags are preserved.

## Tag policy

| Trigger | Docker Hub: `mohammedtv/llmproxy` | GHCR: `ghcr.io/mo7ammedd/llmproxy` | GitHub release |
| --- | --- | --- | --- |
| `main` push | `latest`, `sha-<commit>` | `latest`, `sha-<commit>` | None |
| Stable tag such as `v0.3.0` | `0.3.0`, `0.3`, `latest`, `sha-<commit>` | `v0.3.0`, `v0.3`, `latest`, `sha-<commit>` | Stable |
| Prerelease such as `v0.3.0-rc.1` | `0.3.0-rc.1`, `sha-<commit>` | `v0.3.0-rc.1`, `sha-<commit>` | Prerelease |
| Pull request | None | None | None |

`latest` includes successful main builds and stable release builds. Minor aliases and `latest` move as new builds are published; pin a full version or digest for production. Do not move Git release tags or replace already published full-version images. The `sha-` suffix normally uses the first seven commit characters; the digest is the immutable identifier.

Manual `workflow_dispatch` runs follow the same checks and publication rules for the selected ref. A manual feature-branch run publishes its `sha-` tag, while `main` also updates `latest`. Use a pull request when you only want validation. The Docker Hub overview is synchronized only after a successful `main` publication, so historical tags and feature branches cannot replace the current overview.

The [workflow](../.github/workflows/ci.yml) requires solution/format/database tests, a recovery drill and native ARM64 and AMD64 container/SDK checks before publication. It then scans the published AMD64/ARM64 manifests, verifies the common index, requires SBOMs and build provenance for both platforms, signs both registry digests and verifies both signatures and scan attestations. A tagged release is created only after the image job passes.

## Repository configuration

The maintainer configures these GitHub Actions values under repository **Settings → Secrets and variables → Actions**:

| Type | Name | Value / purpose |
| --- | --- | --- |
| Variable | `DOCKERHUB_USERNAME` | `mohammedtv`; also selects the Docker Hub namespace for `llmproxy` |
| Secret | `DOCKERHUB_TOKEN` | Docker Hub access token with permission to push `llmproxy` and update its overview |
| Built in token | `GITHUB_TOKEN` | Supplied by GitHub; package writes are granted only to the image job, release writes only to the release job; the image job has `id-token: write` for keyless signing |

To configure or rotate the Docker Hub values with `gh`:

```bash
gh variable set DOCKERHUB_USERNAME --repo Mo7ammedd/LLMProxy --body mohammedtv
gh secret set DOCKERHUB_TOKEN --repo Mo7ammedd/LLMProxy
```

The second command prompts without displaying the token. No registry credential belongs in `.env.example`, source, release notes or workflow logs. Pull-request runs skip registry authentication and publication. Missing publication settings fail non-PR image jobs explicitly.

Docker Hub's repository is public. GHCR package visibility is a separate setting; if a package is private, clients need a GitHub token with `read:packages` and access to the package. Publishing does not change visibility settings.

The Docker Hub overview is maintained in [dockerhub.md](dockerhub.md). The publication job updates it through the registry API and reads it back to verify the exact content. Keep overview links absolute so they work on Docker Hub.

## Cutting a release

1. Choose an unused version. Update `Directory.Build.props` and `docs/openapi.yaml`, move the relevant `Unreleased` changelog entries into a dated version section, and review upgrade/migration notes. Update the pinned examples, Compose default and Docker Hub overview when recommending a new stable release.
2. Review the changes through a pull request and wait for CI. Merge the reviewed commit and fetch the current `main`.
3. Create an annotated Git tag on the chosen commit. The workflow validates that the tag matches `Directory.Build.props` exactly. For example, after preparing version 0.3.0:

   ```bash
   git fetch origin
   git tag -a v0.3.0 origin/main -m 'LLMProxy v0.3.0'
   git push origin v0.3.0
   ```

4. Follow the tag's Actions run. Check that both registries contain the expected version and architectures and that the release was created after the checks passed. For a prerelease, use a version such as `0.3.0-rc.1` in source and `v0.3.0-rc.1` as the tag; it will not move stable minor aliases or `latest`.
5. Review the generated GitHub release notes. They include image references, the verified digest, changelog/deployment links and GitHub's change list. Add any release-specific operational instructions.

The application and schema may require coordination across replicas. Follow the [migration and backup procedures](operations.md#migrations-and-upgrades) when upgrading a running deployment.

## Verification and interrupted publication

Inspect published tags with Docker Buildx:

```bash
docker buildx imagetools inspect mohammedtv/llmproxy:0.3.0
docker buildx imagetools inspect ghcr.io/mo7ammedd/llmproxy:v0.3.0
```

Check the source revision label, index digest and `linux/amd64` / `linux/arm64` entries against the release and Actions summary. Use the corresponding registry's digest when pinning the original 0.2.0 images.

Publishing to two registries is not an atomic transaction. If a run fails after one registry has accepted an image, inspect both registries before retrying. Preserve any already published full-version digest; copy that verified image to the missing destination rather than rebuilding a replacement under the same version. If the code or release inputs need changes, choose a new version.

GitHub release creation preserves an existing release and its edited notes on reruns. If only release creation failed after successful verification, rerun that failed job. An overview-only failure can be repaired by rerunning the overview script with the same repository settings; it does not require rebuilding an image.

## Signatures, SBOMs and vulnerability policy

Version 0.3 publications use Cosign 3.1.3 with GitHub Actions OIDC. No long-lived signing key is stored in repository secrets. Signatures bind the multi-platform image digest, including the image and attached BuildKit SBOM/provenance manifests. Actions, build/runtime base images and the SBOM generator are pinned; Dependabot proposes reviewed updates.

Install Cosign 3.1.3 or a compatible newer version. Copy the **index digest** from the release notes or the `image-digest.txt` release asset and verify the exact release workflow identity:

```bash
IMAGE_DIGEST='sha256:REPLACE_WITH_RELEASE_DIGEST'
cosign verify \
  --certificate-identity 'https://github.com/Mo7ammedd/LLMProxy/.github/workflows/ci.yml@refs/tags/v0.3.0' \
  --certificate-oidc-issuer 'https://token.actions.githubusercontent.com' \
  "mohammedtv/llmproxy@$IMAGE_DIGEST"

cosign verify-attestation \
  --type 'https://github.com/Mo7ammedd/LLMProxy/attestations/trivy/v1' \
  --certificate-identity 'https://github.com/Mo7ammedd/LLMProxy/.github/workflows/ci.yml@refs/tags/v0.3.0' \
  --certificate-oidc-issuer 'https://token.actions.githubusercontent.com' \
  "mohammedtv/llmproxy@$IMAGE_DIGEST"
```

Replace the image name with `ghcr.io/mo7ammedd/llmproxy` to verify GHCR. For `main` builds, the expected identity ends with `@refs/heads/main`; release consumers should pin the release identity. Do not disable identity, issuer or transparency verification.

Trivy scans OS and language packages in both native test images before publication, then scans both final platform manifests by digest. The release gate fails on **HIGH or CRITICAL vulnerabilities with a published fix**. Full JSON reports retain all severities and unfixed findings; an accepted scan is not a claim that the image contains no vulnerabilities. Scanner failures and incomplete reports also fail the job. No blanket ignore file is used. The scan attestation includes both full reports, policy, scanner version, source SHA and index digest. Security databases change over time; scan results describe the database used during that build.

Every successful release attaches `sbom-amd64.spdx.json`, `sbom-arm64.spdx.json`, `provenance.json`, full `scan-*.json` reports, scan predicate, verification output, `image-digest.txt` and `SHA256SUMS`. Download the assets together and run `sha256sum -c SHA256SUMS` in that directory. The attached checksums detect corruption; use Cosign for origin verification. SBOMs can also be read from the registry:

```bash
docker buildx imagetools inspect "mohammedtv/llmproxy@$IMAGE_DIGEST" --format '{{json .SBOM}}'
docker buildx imagetools inspect "mohammedtv/llmproxy@$IMAGE_DIGEST" --format '{{json .Provenance}}'
```

The main branch requires `verify`, `runtime-arm64` and `image`, an up-to-date pull request and resolved conversations, including for administrators. Force pushes and deletions are disabled. The single-owner repository does not require a second human approval. Secret scanning, push protection, private vulnerability reporting and automated dependency security updates are enabled. Report vulnerabilities through the repository's private security reporting page.

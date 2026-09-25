#!/usr/bin/env bash
set -euo pipefail
export GH_HTTP_TIMEOUT=30
curl_read=(--connect-timeout 10 --max-time 60 --retry 2 --retry-delay 1 --retry-connrefused)
cd "$PUBLISH_ROOT"
PUBLISH_ROOT="$(pwd)"
metadata="build-metadata.json"
test "$(jq -r .sourceSha "$metadata")" = "$SOURCE_SHA"
test "$(jq -r .tag "$metadata")" = "$TAG"
test "$(jq -r .mode "$metadata")" = production
zip_asset="NexusPipeline-$(jq -r .tag "$metadata")-win-x64.zip"
sha_asset="$zip_asset.sha256"
setup_asset="NexusPipeline-$(jq -r .tag "$metadata")-win-x64-setup.exe"
setup_sha_asset="$setup_asset.sha256"
test -f "$zip_asset" -a -f "$sha_asset" -a -f "$setup_asset" -a -f "$setup_sha_asset" -a -f installer-build-metadata.json
expected="$(tr -d '\r\n' < "$sha_asset")"
actual="$(sha256sum "$zip_asset" | cut -d' ' -f1)"
test "$expected" = "$actual"
test "$(tr -d '\r\n' < "$setup_sha_asset")" = "$(sha256sum "$setup_asset" | cut -d' ' -f1)"
python "$GITHUB_WORKSPACE/NexusPipeline/tools/host_release.py" verify-package \
  --package "$zip_asset" \
  --metadata "$metadata" \
  --expected-source-sha "$SOURCE_SHA" \
  --expected-tag "$TAG"
python "$GITHUB_WORKSPACE/NexusPipeline/tools/host_release.py" verify-installer \
  --root "$GITHUB_WORKSPACE/NexusPipeline-Source" \
  --output "$PUBLISH_ROOT" \
  --expected-source-sha "$SOURCE_SHA" \
  --expected-tag "$TAG"
api="https://api.github.com/repos/$GH_REPO/releases/tags/$TAG"
response_file="$(mktemp)"
status="$(curl "${curl_read[@]}" -sS -o "$response_file" -w '%{http_code}' -H "Authorization: Bearer $GH_TOKEN" -H 'Accept: application/vnd.github+json' "$api")"
if [ "$status" = 404 ]; then
  # Tag lookup omits drafts; recover an existing draft by its immutable ID.
  matches="$(gh api --paginate "repos/$GH_REPO/releases?per_page=100" | jq -s --arg tag "$TAG" '[.[][] | select(.tag_name == $tag)]')"
  count="$(jq length <<<"$matches")"
  test "$count" -le 1 || { echo "同 tag 存在多个 Release，拒绝猜测恢复目标" >&2; exit 1; }
  if [ "$count" = 1 ]; then
    release_json="$(jq '.[0]' <<<"$matches")"
  else
    prerelease=false
    if [[ "$TAG" =~ ^v0\. || "$TAG" =~ -(beta|rc)\.[0-9]+$ ]]; then prerelease=true; fi
    release_json="$(gh api --method POST "repos/$GH_REPO/releases" -f tag_name="$TAG" -f target_commitish="$SOURCE_SHA" -f name="$TAG" -f body="sourceSha=$SOURCE_SHA" -F draft=true -F prerelease="$prerelease")"
  fi
elif [ "$status" = 200 ]; then
  release_json="$(cat "$response_file")"
else
  cat "$response_file" >&2
  echo "读取同 tag Release 失败：HTTP $status" >&2
  exit 1
fi
test "$(jq -r .tag_name <<<"$release_json")" = "$TAG" || { echo "Release tag 身份冲突" >&2; exit 1; }
test "$(jq -r .target_commitish <<<"$release_json")" = "$SOURCE_SHA" || { echo "Release source 身份冲突" >&2; exit 1; }
release_id="$(jq -r .id <<<"$release_json")"
test "$release_id" != null -a "$release_id" != ""
for asset in "$zip_asset" "$sha_asset" "$setup_asset" "$setup_sha_asset"; do
  existing_url="$(jq -r --arg name "$asset" '.assets[]? | select(.name == $name) | .url' <<<"$release_json" | head -n 1)"
  if [ -n "$existing_url" ]; then
    existing="$(mktemp)"
    curl "${curl_read[@]}" -sS -L -H "Authorization: Bearer $GH_TOKEN" -H 'Accept: application/octet-stream' "$existing_url" -o "$existing"
    cmp -s "$existing" "$asset" || { echo "拒绝覆盖同 tag 的不同字节资产：$asset" >&2; exit 1; }
    rm -f "$existing"
  else
    gh release upload "$TAG" "$asset" --repo "$GH_REPO"
  fi
done
release_json="$(gh api "repos/$GH_REPO/releases/$release_id")"
for asset in "$zip_asset" "$sha_asset" "$setup_asset" "$setup_sha_asset"; do
  asset_url="$(jq -r --arg name "$asset" '.assets[]? | select(.name == $name) | .url' <<<"$release_json" | head -n 1)"
  test -n "$asset_url"
  received="$(mktemp)"
  curl "${curl_read[@]}" -sS -f -L -H "Authorization: Bearer $GH_TOKEN" -H 'Accept: application/octet-stream' "$asset_url" -o "$received"
  cmp -s "$received" "$asset" || { echo "远端资产下载后字节不一致：$asset" >&2; exit 1; }
  if [ "$asset" = "$zip_asset" ]; then
    python "$GITHUB_WORKSPACE/NexusPipeline/tools/host_release.py" verify-package \
      --package "$received" \
      --metadata "$metadata" \
      --expected-source-sha "$SOURCE_SHA" \
      --expected-tag "$TAG"
  elif [ "$asset" = "$setup_asset" ]; then
    python "$GITHUB_WORKSPACE/NexusPipeline/tools/host_release.py" verify-installer \
      --root "$GITHUB_WORKSPACE/NexusPipeline-Source" \
      --output "$PUBLISH_ROOT" \
      --package "$received" \
      --expected-source-sha "$SOURCE_SHA" \
      --expected-tag "$TAG"
  else
    test "$(sha256sum "$received" | cut -d' ' -f1)" = "$(sha256sum "$asset" | cut -d' ' -f1)"
  fi
  rm -f "$received"
done
gh api --method PATCH "repos/$GH_REPO/releases/$release_id" -F draft=false
test "$(gh api "repos/$GH_REPO/releases/$release_id" --jq '.draft')" = false

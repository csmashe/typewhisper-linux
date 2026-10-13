#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="${1:-${TMPDIR:-/tmp}/typewhisper-sherpa-webgpu}"
output_dir="${2:-$repo_root/plugins/TypeWhisper.Plugin.SherpaOnnx/Runtimes/linux-x64/sherpa-webgpu}"
sherpa_tag="v1.12.23"
ort_version="1.24.4"
ort_archive="onnxruntime-linux-x64-${ort_version}.tgz"
ort_archive_sha="3a211fbea252c1e66290658f1b735b772056149f28321e71c308942cdb54b747"
webgpu_package="microsoft.ml.onnxruntime.ep.webgpu.0.1.0.nupkg"
webgpu_package_sha="1903686e76064e0f91a0439b4c629386c287ad3bc5d17635fad1f69eb9b18197"

mkdir -p "$work_dir"
if [[ ! -d "$work_dir/sherpa-onnx/.git" ]]; then
  git clone --depth 1 --branch "$sherpa_tag" --recurse-submodules \
    https://github.com/k2-fsa/sherpa-onnx.git "$work_dir/sherpa-onnx"
fi
if ! git -C "$work_dir/sherpa-onnx" apply --reverse --check \
  "$repo_root/scripts/patches/sherpa-onnx-webgpu.patch" >/dev/null 2>&1; then
  git -C "$work_dir/sherpa-onnx" apply --check \
    "$repo_root/scripts/patches/sherpa-onnx-webgpu.patch"
  git -C "$work_dir/sherpa-onnx" apply \
    "$repo_root/scripts/patches/sherpa-onnx-webgpu.patch"
fi

curl --fail --location --retry 3 \
  "https://github.com/microsoft/onnxruntime/releases/download/v${ort_version}/${ort_archive}" \
  --output "$work_dir/$ort_archive"
printf '%s  %s\n' "$ort_archive_sha" "$work_dir/$ort_archive" | sha256sum --check --status
mkdir -p "$work_dir/onnxruntime"
tar -xzf "$work_dir/$ort_archive" -C "$work_dir/onnxruntime" --strip-components=1

curl --fail --location --retry 3 \
  "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.ep.webgpu/0.1.0/${webgpu_package}" \
  --output "$work_dir/$webgpu_package"
printf '%s  %s\n' "$webgpu_package_sha" "$work_dir/$webgpu_package" | sha256sum --check --status
mkdir -p "$work_dir/webgpu-package"
python3 - "$work_dir/$webgpu_package" "$work_dir/webgpu-package" <<'PY'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1]) as package:
    for name in package.namelist():
        if name.startswith("runtimes/linux-x64/native/") and name.endswith(".so"):
            package.extract(name, sys.argv[2])
PY

export SHERPA_ONNXRUNTIME_INCLUDE_DIR="$work_dir/onnxruntime/include"
export SHERPA_ONNXRUNTIME_LIB_DIR="$work_dir/onnxruntime/lib"
cmake -S "$work_dir/sherpa-onnx" -B "$work_dir/build" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SKIP_INSTALL_RPATH=ON \
  -DCMAKE_CXX_FLAGS=-DSHERPA_ONNX_ENABLE_WEBGPU=1 \
  -DBUILD_SHARED_LIBS=ON \
  -DSHERPA_ONNX_ENABLE_C_API=ON \
  -DSHERPA_ONNX_ENABLE_BINARY=OFF \
  -DSHERPA_ONNX_BUILD_TESTS=OFF \
  -DSHERPA_ONNX_BUILD_C_API_EXAMPLES=OFF \
  -DSHERPA_ONNX_USE_PRE_INSTALLED_ONNXRUNTIME_IF_AVAILABLE=ON
cmake --build "$work_dir/build" --parallel "$(nproc)" \
  --target sherpa-onnx-c-api sherpa-onnx-cxx-api
cmake --install "$work_dir/build" --prefix "$work_dir/install"

rm -rf "$output_dir"
mkdir -p "$output_dir"
find "$work_dir/install/lib" -maxdepth 1 \( -type f -o -type l \) \
  -name 'libsherpa-onnx-*.so*' -exec cp -a '{}' "$output_dir/" \;
find "$work_dir/onnxruntime/lib" -maxdepth 1 \( -type f -o -type l \) \
  -name 'libonnxruntime.so*' -exec cp -a '{}' "$output_dir/" \;
find "$work_dir/onnxruntime/lib" -maxdepth 1 \( -type f -o -type l \) \
  -name 'libonnxruntime_providers_shared.so' -exec cp -a '{}' "$output_dir/" \;
find "$work_dir/webgpu-package/runtimes/linux-x64/native" -maxdepth 1 \
  -type f -name '*.so' -exec cp -a '{}' "$output_dir/" \;

for library in libsherpa-onnx-c-api.so libsherpa-onnx-cxx-api.so \
  libonnxruntime.so libonnxruntime.so.1 libonnxruntime_providers_shared.so \
  libonnxruntime_providers_webgpu.so; do
  test -e "$output_dir/$library" || {
    echo "Missing WebGPU runtime library: $output_dir/$library" >&2
    exit 1
  }
done
printf 'Built Sherpa WebGPU runtime in %s\n' "$output_dir"

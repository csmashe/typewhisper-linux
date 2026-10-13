#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="${1:-${TMPDIR:-/tmp}/typewhisper-sherpa-migraphx}"
output_dir="${2:-$repo_root/plugins/TypeWhisper.Plugin.SherpaOnnx/Runtimes/linux-x64/sherpa-migraphx}"
sherpa_tag="v1.12.23"
ort_version="1.23.2"
ort_wheel="onnxruntime_migraphx-${ort_version}-cp312-cp312-manylinux_2_27_x86_64.manylinux_2_28_x86_64.whl"
ort_wheel_sha="663bff4dc3f72582d69f12ad073eb5695dfb526d574376cc8e5b161c7d2f0f08"

mkdir -p "$work_dir"
if [[ ! -d "$work_dir/sherpa-onnx/.git" ]]; then
  rm -rf "$work_dir/sherpa-onnx"
  git clone --depth 1 --branch "$sherpa_tag" --recurse-submodules \
    https://github.com/k2-fsa/sherpa-onnx.git "$work_dir/sherpa-onnx"
fi
if ! git -C "$work_dir/sherpa-onnx" apply --reverse --check "$repo_root/scripts/patches/sherpa-onnx-migraphx.patch"; then
  git -C "$work_dir/sherpa-onnx" apply --check "$repo_root/scripts/patches/sherpa-onnx-migraphx.patch"
  git -C "$work_dir/sherpa-onnx" apply "$repo_root/scripts/patches/sherpa-onnx-migraphx.patch"
fi

curl --fail --location --retry 3 \
  "https://github.com/microsoft/onnxruntime/releases/download/v${ort_version}/onnxruntime-linux-x64-${ort_version}.tgz" \
  --output "$work_dir/onnxruntime.tgz"
mkdir -p "$work_dir/onnxruntime"
tar -xzf "$work_dir/onnxruntime.tgz" -C "$work_dir/onnxruntime" --strip-components=1

curl --fail --location --retry 3 \
  "https://repo.radeon.com/rocm/manylinux/rocm-rel-7.2.1/${ort_wheel}" \
  --output "$work_dir/$ort_wheel"
printf '%s  %s\n' "$ort_wheel_sha" "$work_dir/$ort_wheel" | sha256sum --check --status
mkdir -p "$work_dir/migraphx-ort"
python3 - "$work_dir/$ort_wheel" "$work_dir/migraphx-ort" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as wheel:
    for name in wheel.namelist():
        if name.endswith(("libonnxruntime.so.1.23.2", "libonnxruntime_providers_shared.so")):
            wheel.extract(name, sys.argv[2])
PY
ort_lib_dir="$(find "$work_dir/migraphx-ort" -type f -name 'libonnxruntime.so.1.23.2' -printf '%h\n' -quit)"
test -n "$ort_lib_dir"
ln -sf libonnxruntime.so.1.23.2 "$ort_lib_dir/libonnxruntime.so"

export SHERPA_ONNXRUNTIME_INCLUDE_DIR="$work_dir/onnxruntime/include"
export SHERPA_ONNXRUNTIME_LIB_DIR="$ort_lib_dir"
cmake -S "$work_dir/sherpa-onnx" -B "$work_dir/build" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SKIP_INSTALL_RPATH=ON \
  -DBUILD_SHARED_LIBS=ON \
  -DSHERPA_ONNX_ENABLE_C_API=ON \
  -DSHERPA_ONNX_ENABLE_BINARY=OFF \
  -DSHERPA_ONNX_BUILD_TESTS=OFF \
  -DSHERPA_ONNX_BUILD_C_API_EXAMPLES=OFF \
  -DSHERPA_ONNX_USE_PRE_INSTALLED_ONNXRUNTIME_IF_AVAILABLE=ON
cmake --build "$work_dir/build" --parallel "$(nproc)" --target sherpa-onnx-c-api sherpa-onnx-cxx-api
cmake --install "$work_dir/build" --prefix "$work_dir/install"

rm -rf "$output_dir"
mkdir -p "$output_dir"
find "$work_dir/install/lib" -maxdepth 1 \( -type f -o -type l \) -name '*.so*' \
  ! -name 'libonnxruntime*' -exec cp -a '{}' "$output_dir/" \;
test -f "$output_dir/libsherpa-onnx-c-api.so"
test -f "$output_dir/libsherpa-onnx-cxx-api.so"
printf 'Built MIGraphX Sherpa runtime in %s\n' "$output_dir"

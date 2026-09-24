#!/usr/bin/env bash
# setup.sh - 在干净的 Linux (x64) 环境里搭好 repkg-Re 的开发/构建环境
# 用法: bash setup.sh          # 只装 SDK 并 build + test
#       bash setup.sh --aot    # 额外构建 linux-x64 NativeAOT 二进制
# 幂等：已装过的步骤会跳过。
set -euo pipefail

DOTNET_DIR="${DOTNET_DIR:-/usr/local/dotnet}"
DOTNET_VERSION="${DOTNET_VERSION:-10.0}"
REPO="${REPO:-https://github.com/ReZe20/repkg-Re}"
DIR="${DIR:-$(pwd)/repkg-Re}"
AOT=0
[[ "${1:-}" == "--aot" ]] && AOT=1

SUDO=""
if [[ $(id -u) != 0 ]]; then
  command -v sudo >/dev/null && SUDO="sudo"
fi

echo "==> 1/4 依赖"
if ! command -v git >/dev/null; then
  $SUDO apt-get update -qq && $SUDO apt-get install -y -qq git ca-certificates curl libicu72 zlib1g
fi

echo "==> 2/4 .NET SDK ${DOTNET_VERSION} -> ${DOTNET_DIR}"
if [[ ! -x "$DOTNET_DIR/dotnet" ]]; then
  tmp=$(mktemp -d)
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp/dotnet-install.sh"
  bash "$tmp/dotnet-install.sh" --channel "$DOTNET_VERSION" --install-dir "$DOTNET_DIR"
  rm -rf "$tmp"
fi
export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$PATH"
"$DOTNET_DIR/dotnet" --version

echo "==> 3/4 仓库"
if [[ ! -d "$DIR/.git" ]]; then
  git clone "$REPO" "$DIR"
fi
cd "$DIR"

echo "==> 4/4 构建 + 测试"
"$DOTNET_DIR/dotnet" build -c Release --nologo
"$DOTNET_DIR/dotnet" test --nologo

if [[ $AOT == 1 ]]; then
  echo "==> NativeAOT (linux-x64) 需要 clang + zlib 开发包"
  $SUDO apt-get update -qq
  $SUDO apt-get install -y -qq clang zlib1g-dev
  "$DOTNET_DIR/dotnet" publish RePKG_Re/RePKG_Re.csproj -p:PublishProfile=AotLinuxX64 --nologo
  BIN="RePKG_Re/bin/publish-aot-linux/RePKG_Re"
  ls -la "$BIN"
  "$BIN" --version
  "$BIN" batch --help >/dev/null
  echo "==> AOT 产物: $DIR/$BIN"
fi

echo "==> 完成。"

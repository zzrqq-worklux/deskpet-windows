#!/bin/bash
# tools/build_dmg.sh — 构建 deskpet.app 并打包成 DMG（本机运行用）
#
# 前置条件：必须装完整 Xcode（App Store 下载）。仅 Command Line Tools 不行。
#   安装后执行一次：sudo xcode-select -s /Applications/Xcode.app
#
# 用法:
#   ./tools/build_dmg.sh              # 构建 + 生成 DeskPet-<版本>.dmg
#   ./tools/build_dmg.sh --install    # 构建 + 打 DMG + 装到 /Applications
#   ./tools/build_dmg.sh --run        # 只构建并直接运行 .app（不生成 DMG，测试用）
#
# 说明: 使用 ad-hoc 签名（本机构建的本机应用不会触发 Gatekeeper 拦截，
#       无需 xattr 去隔离属性）。若要把 dmg 发给别人，需换成开发者证书签名。

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCHEME="${SCHEME:-deskpet}"
CONFIG="${CONFIG:-Debug}"
VERSION="${VERSION:-0.1.0}"
DMG_NAME="${DMG_NAME:-DeskPet}"
INSTALL=0
RUN_MODE=0
for arg in "$@"; do
  [[ "$arg" == "--install" ]] && INSTALL=1
  [[ "$arg" == "--run" ]] && RUN_MODE=1
done

# ---- 1. 检查 Xcode（不能是 CommandLineTools）----
XCODE_PATH="$(xcode-select -p 2>/dev/null || true)"
if [[ -z "$XCODE_PATH" || "$XCODE_PATH" == *CommandLineTools* ]]; then
  echo "❌ 当前没有完整 Xcode（只有 Command Line Tools）。" >&2
  echo "   请从 Mac App Store 安装 Xcode，然后执行：sudo xcode-select -s /Applications/Xcode.app" >&2
  exit 1
fi
echo "✅ Xcode: $XCODE_PATH"
XCODE_VERSION="$(xcodebuild -version 2>/dev/null)"
printf '%s\n' "${XCODE_VERSION%%$'\n'*}"

# ---- 2. 构建（ad-hoc 签名 "-"）----
echo "==> 构建 $SCHEME ($CONFIG)…"
xcodebuild \
  -project "$ROOT/deskpet.xcodeproj" \
  -scheme "$SCHEME" \
  -configuration "$CONFIG" \
  -destination 'generic/platform=macOS' \
  CODE_SIGNING_ALLOWED=YES \
  CODE_SIGN_IDENTITY="-" \
  ENABLE_HARDENED_RUNTIME=NO \
  build

# ---- 3. 定位 .app ----
BUILD_DIR="$(xcodebuild \
  -project "$ROOT/deskpet.xcodeproj" \
  -scheme "$SCHEME" \
  -configuration "$CONFIG" \
  -showBuildSettings 2>/dev/null \
  | awk '/ TARGET_BUILD_DIR =/{print $3; exit}')"
APP_PATH="$BUILD_DIR/deskpet.app"
if [[ ! -d "$APP_PATH" ]]; then
  echo "❌ 未找到构建产物: $APP_PATH" >&2
  exit 1
fi
echo "✅ App: $APP_PATH"

# ---- 4. --run：不打包 DMG，直接启动 .app（测试用）----
if [[ $RUN_MODE == 1 ]]; then
  echo "==> 直接启动 .app…"
  open "$APP_PATH"
  echo "✅ 已启动 $APP_PATH （若想停止，请到菜单栏图标退出，或: pkill -x deskpet）"
  echo "✅ 完成（未生成 DMG）"
  exit 0
fi

# ---- 5. 打包 DMG（staging: 应用 + /Applications 快捷方式）----
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/dmg"
cp -R "$APP_PATH" "$STAGE/dmg/"
ln -s /Applications "$STAGE/dmg/Applications"
OUT="$ROOT/${DMG_NAME}-${VERSION}.dmg"
echo "==> 生成 DMG: $OUT"
hdiutil create -volname "$DMG_NAME" -srcfolder "$STAGE/dmg" -ov -format UDZO "$OUT" >/dev/null
echo "✅ DMG 生成完毕: $OUT"

# ---- 6. （可选）装到 /Applications ----
if [[ $INSTALL == 1 ]]; then
  echo "==> 安装到 /Applications…"
  rm -rf "/Applications/deskpet.app"
  cp -R "$APP_PATH" "/Applications/"
  echo "✅ 已安装。现在可以从启动台/访达打开 DeskPet。"
fi

echo "✅ 全部完成"

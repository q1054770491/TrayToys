#!/bin/bash
# ============================================================
# release.sh —— 版本发布 / 源码归档
#
# 【规则】用户约定（2026-10-19 起生效）：
#   · 日常改代码**不升版本号**，继续沿用当前已发布的版本号；
#   · 只有用户明确说「发布新版本」时才升版本号；
#   · 每次发布，把该版本的**完整源码**封存到 archive/v<版本>/。
#
# 【用法】
#   bash release.sh archive                只把"当前版本"的源码封存一份（不升版本、不构建）
#   bash release.sh archive "说明"          同上，并更新该版本的说明（用于重发同一版本）
#   bash release.sh 2.1                    发布新版本 2.1：改版本号 → 构建 → 归档源码
#   bash release.sh 2.1 "修复看视频误变暗"   同上，并写入版本说明
# ============================================================
set -e
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'
cd "$(dirname "$0")"

APPINFO="src/AppInfo.cs"
ASMINFO="src/AssemblyInfo.cs"

# 程序名也从 AppInfo.cs 读取，改名后这里不用动
NAME=$(sed -n 's/.*Name = "\(.*\)".*/\1/p' "$APPINFO" | head -1)
if [ -z "$NAME" ]; then
  echo "错误：无法从 $APPINFO 读取程序名 Name"
  exit 1
fi

CUR=$(sed -n 's/.*Version = "\([0-9][0-9.]*\)".*/\1/p' "$APPINFO" | head -1)
if [ -z "$CUR" ]; then
  echo "错误：无法从 $APPINFO 读取当前版本号"
  exit 1
fi

# 把一个版本的完整源码快照到 archive/v<版本>/
# $1 = 版本号, $2 = 版本说明, $3 = "1" 表示强制使用 $2（不沿用旧说明）
archive_version() {
  local ver="$1"
  local desc="$2"
  local forceDesc="$3"
  local dst="archive/v${ver}"

  # 已有快照时，默认保留它原来的「版本说明」，避免重新归档把发布时写好的说明冲掉。
  # 但显式传了说明（forceDesc=1，即"重发同一版本"场景）时，以新说明为准。
  if [ "$forceDesc" != "1" ] && [ -f "$dst/RELEASE.txt" ]; then
    local prevDesc
    prevDesc=$(sed -n 's/^版本说明 : //p' "$dst/RELEASE.txt" | head -1)
    if [ -n "$prevDesc" ] && [ "$prevDesc" != "（未填写）" ]; then
      desc="$prevDesc"
    fi
  fi

  if [ -d "$dst" ]; then
    echo "提示：${dst} 已存在，将覆盖"
    rm -rf "$dst"
  fi
  mkdir -p "$dst/src"
  cp src/*.cs "$dst/src/"
  for f in "${NAME}.csproj" "${NAME}.sln" build.sh build.bat set-startup-shortcut.ps1 README.md; do
    [ -f "$f" ] && cp "$f" "$dst/"
  done

  # 把该版本的可执行文件也存进快照。发布流程里此时已经构建好；
  # 单独执行 `release.sh archive` 时若还没构建过，就只存源码。
  local exeInfo="未包含（归档时 bin/ 下没有该版本的 exe）"
  if [ -f "bin/${NAME}_v${ver}.exe" ]; then
    cp "bin/${NAME}_v${ver}.exe" "$dst/"
    exeInfo="同目录 ${NAME}_v${ver}.exe（$(du -k "bin/${NAME}_v${ver}.exe" | cut -f1) KB）"
  fi

  local cnt
  cnt=$(ls src/*.cs 2>/dev/null | wc -l | tr -d ' ')
  cat > "$dst/RELEASE.txt" <<EOF
版本号   : ${ver}
归档时间 : $(date '+%Y-%m-%d %H:%M:%S')
版本说明 : ${desc:-（未填写）}
源文件   : src/*.cs 共 ${cnt} 个
可执行   : ${exeInfo}

【本目录内容】
  src/                     该版本的完整源码
  ${NAME}_v${ver}.exe 该版本的成品（若显示未包含则需重新构建）
  build.sh / build.bat     构建脚本（相对路径设计，可在此目录直接运行）
  RELEASE.txt              本说明

【如何重建】
  cd <本目录>
  bash build.sh          # Windows 下也可双击 build.bat
  # 产物：bin/${NAME}_v${ver}.exe
EOF

  echo "已归档 v${ver}（源码 + exe）→ ${dst}/"
}

# ---------- 模式一：只归档当前版本 ----------
# 可选第二个参数用作版本说明（用于"重发同一版本"：覆盖 exe 与快照并更新说明）
if [ "$1" = "archive" ]; then
  if [ -n "$2" ]; then
    archive_version "$CUR" "$2" 1
  else
    archive_version "$CUR" "手动归档（补齐历史快照）" 0
  fi
  echo "当前版本 v${CUR} 的源码已封存（版本号未改动）"
  exit 0
fi

# ---------- 无参数：打印用法 ----------
if [ -z "$1" ]; then
  echo "当前版本: v${CUR}"
  echo ""
  echo "用法:"
  echo "  bash release.sh archive              只归档当前版本源码（不升版本）"
  echo "  bash release.sh <新版本号> [说明]    发布新版本（改版本号 → 构建 → 归档）"
  echo ""
  echo "已归档的版本:"
  ls -1 archive 2>/dev/null | sed 's/^/  /' || echo "  （暂无）"
  exit 0
fi

# ---------- 模式二：发布新版本 ----------
NEW="$1"
DESC="${2:-}"

if ! echo "$NEW" | grep -qE '^[0-9]+(\.[0-9]+)*$'; then
  echo "错误：版本号格式不合法（示例：2.1 / 2.1.3）。收到：$NEW"
  exit 1
fi
if [ "$NEW" = "$CUR" ]; then
  echo "错误：新版本号与当前版本相同（v${CUR}）"
  exit 1
fi

# AssemblyVersion 需要 4 段
case $(echo "$NEW" | awk -F. '{print NF}') in
  1) AV="${NEW}.0.0.0" ;;
  2) AV="${NEW}.0.0" ;;
  3) AV="${NEW}.0" ;;
  *) AV="$NEW" ;;
esac

echo "=========================================="
echo "发布新版本: v${CUR} → v${NEW}"
echo "=========================================="

# 备份版本文件，构建失败时回滚
cp "$APPINFO" "${APPINFO}.bak"
cp "$ASMINFO" "${ASMINFO}.bak"

restore_version_files() {
  mv -f "${APPINFO}.bak" "$APPINFO" 2>/dev/null || true
  mv -f "${ASMINFO}.bak" "$ASMINFO" 2>/dev/null || true
}

# 1) 改版本号
sed -i "s/Version = \"[0-9.]*\"/Version = \"${NEW}\"/" "$APPINFO"
sed -i "s/AssemblyVersion(\"[0-9.]*\")/AssemblyVersion(\"${AV}\")/" "$ASMINFO"
sed -i "s/AssemblyFileVersion(\"[0-9.]*\")/AssemblyFileVersion(\"${AV}\")/" "$ASMINFO"
echo "[1/3] 版本号已更新 → v${NEW} (assembly ${AV})"

# 2) 构建
echo "[2/3] 构建中…"
if ! bash build.sh; then
  echo "构建失败，正在回滚版本号…"
  restore_version_files
  echo "已回滚为 v${CUR}，请修复编译错误后重试"
  exit 1
fi
rm -f "${APPINFO}.bak" "${ASMINFO}.bak"

# 3) 归档该版本源码
echo "[3/3] 归档源码…"
archive_version "$NEW" "$DESC"

echo "=========================================="
echo "发布完成: v${NEW}"
echo "  exe    : bin/${NAME}_v${NEW}.exe"
echo "  源码   : archive/v${NEW}/"
echo "  启动项 : 已指向新版本"
echo "=========================================="

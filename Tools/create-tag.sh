#!/bin/bash

# ============================================
# 自动化 Git Tag 创建脚本
# 功能：创建带有提交次数的版本标签 v1.0.0.123
# 要求：工作区必须干净才能创建
# 用法：./create-git-tag.sh <版本号>
# 示例：./create-git-tag.sh 1.0.0
# ============================================

# 设置错误处理
set -e  # 任何命令失败时退出脚本
set -o pipefail  # 管道中任何命令失败都认为失败

# 颜色定义
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# 打印颜色输出函数
print_error() {
    echo -e "${RED}[错误] $1${NC}"
}

print_success() {
    echo -e "${GREEN}[成功] $1${NC}"
}

print_warning() {
    echo -e "${YELLOW}[警告] $1${NC}"
}

print_info() {
    echo -e "${BLUE}[信息] $1${NC}"
}

# 检查参数
if [ $# -eq 0 ]; then
    print_error "请提供版本号参数！"
    echo "用法: $0 <版本号>"
    echo "示例: $0 1.0.0"
    exit 1
fi

VERSION="$1"

# 验证版本号格式 (基本验证)
if ! [[ $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    print_error "版本号格式不正确！请使用语义化版本号，如：1.0.0、2.1.5"
    echo "支持的格式：主版本号.次版本号.修订号"
    exit 1
fi

# 检查 Git 命令是否可用
if ! command -v git &> /dev/null; then
    print_error "Git 命令未找到，请确保 Git 已安装并添加到 PATH"
    exit 1
fi

# 检查是否在 Git 仓库中
if ! git rev-parse --git-dir > /dev/null 2>&1; then
    print_error "当前目录不是 Git 仓库！"
    exit 1
fi

# 检查工作区是否干净
print_info "检查工作区状态..."
if [ -n "$(git status --porcelain)" ]; then
    print_error "工作区不干净！请先提交或暂存所有更改。"
    echo "当前状态："
    git status --short
    echo ""
    echo "你可以选择："
    echo "  1. 提交所有更改：git add . && git commit -m '你的提交信息'"
    echo "  2. 暂存更改：git stash"
    echo "  3. 放弃更改（危险）：git checkout -- ."
    exit 1
fi

print_success "工作区是干净的。"

# 获取当前分支
CURRENT_BRANCH=$(git symbolic-ref --short HEAD 2>/dev/null || echo "detached HEAD")
print_info "当前分支：$CURRENT_BRANCH"

# 获取提交次数（从第一次提交开始计数）
print_info "正在计算提交次数..."
TOTAL_COMMITS=$(git rev-list --count --no-merges HEAD 2>/dev/null)

if [ -z "$TOTAL_COMMITS" ] || [ "$TOTAL_COMMITS" -eq 0 ]; then
    print_error "无法获取提交次数，或者仓库为空"
    exit 1
fi

# 创建完整的 tag 名称
TAG_NAME="v${VERSION}-r${TOTAL_COMMITS}"
print_info "准备创建标签：$TAG_NAME"

# 检查 tag 是否已存在
if git rev-parse "$TAG_NAME" >/dev/null 2>&1; then
    print_error "标签 '$TAG_NAME' 已经存在！"
    echo "已存在的标签列表（过滤 v${VERSION}.*）："
    git tag -l "v${VERSION}.*" | sort -V
    exit 1
fi

# 获取当前提交的哈希和消息
COMMIT_HASH=$(git rev-parse --short HEAD)
COMMIT_MESSAGE=$(git log -1 --pretty=%B | head -n1)

# 显示要创建的 tag 信息
echo ""
echo "==============================="
echo "标签创建信息"
echo "==============================="
echo "标签名称：$TAG_NAME"
echo "提交次数：$TOTAL_COMMITS"
echo "提交哈希：$COMMIT_HASH"
echo "提交消息：$COMMIT_MESSAGE"
echo "当前分支：$CURRENT_BRANCH"
echo "==============================="
echo ""

# 创建本地标签
print_info "正在创建本地标签..."
if git tag -a "$TAG_NAME" -m "Release version ${VERSION} (commit ${TOTAL_COMMITS})

Commit: ${COMMIT_HASH}
Branch: ${CURRENT_BRANCH}
Date: $(date '+%Y-%m-%d %H:%M:%S')

${COMMIT_MESSAGE}"; then
    print_success "本地标签创建成功：$TAG_NAME"
else
    print_error "创建标签失败！"
    exit 1
fi

# 显示所有相关标签
echo ""
print_info "当前版本相关标签列表："
git tag -l "v${VERSION}.*" | sort -V

# 显示最新创建的标签
echo ""
print_info "最新创建的标签详细信息："
git show "$TAG_NAME" --stat | head -20

# 可选：创建版本文件（如果需要）
create_version_file() {
    VERSION_FILE="VERSION"
    echo "$TAG_NAME" > "$VERSION_FILE"
    echo "commit: $COMMIT_HASH" >> "$VERSION_FILE"
    echo "date: $(date '+%Y-%m-%d %H:%M:%S')" >> "$VERSION_FILE"
    git add "$VERSION_FILE"
    git commit -m "chore: update version file to $TAG_NAME" > /dev/null 2>&1
    print_success "已创建版本文件：$VERSION_FILE"
}

echo ""
print_success "✅ 标签创建流程完成！"
echo "标签：$TAG_NAME"
echo "可在 CI/CD 中使用此标签进行构建和部署"

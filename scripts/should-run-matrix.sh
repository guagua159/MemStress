#!/usr/bin/env bash
# 判断 63 格故障注入矩阵这次要不要跑。
#
# 矩阵校验的是「7 种模式 × 9 种故障能否检出」——检测力的回归。
# 只有可能改变这个结论的改动才需要跑；文档、界面、外部工具、报告排版
# 不改变检测力，跳过不损失信息。
#
# 判断不出结果时一律「保守全跑」：宁可多花 2 分钟，也不能让一次真实回归
# 因为过滤逻辑的漏洞被静默放过。这是「测不到就说测不到」的延伸——
# 不确定该不该测的时候，测。
#
# 用法：
#   EVENT=push BASE=<before-sha> HEAD=<sha> bash scripts/should-run-matrix.sh
#   EVENT=pull_request BASE=origin/main HEAD=<sha> bash scripts/should-run-matrix.sh
#   EVENT=schedule bash scripts/should-run-matrix.sh
#   EVENT=workflow_dispatch FORCE=true bash scripts/should-run-matrix.sh
#
# 输出两行，供 CI 解析：
#   run=true|false
#   reason=<中文说明>
set -u

# 相关文件：改变「怎么读写校验」「怎么注入故障」「怎么记错误」「怎么解析参数」
# 「怎么构建」的改动。锚定整条路径，避免 Core/ 下无关文件被误判。
SENSITIVE='^(src/MemStress/Core/(StressEngine[^/]*|Patterns|TestOptions|MemoryArena|Native|LogWriter|MemoryError)\.cs|src/MemStress/SelfTest[^/]*\.cs|src/MemStress/MemStress\.csproj|scripts/matrix-test\.sh|\.github/workflows/ci\.yml)$'

EVENT=${EVENT:-}
BASE=${BASE:-}
HEAD=${HEAD:-}
FORCE=${FORCE:-false}

emit() {
  echo "run=$1"
  echo "reason=$2"
}

case "$EVENT" in
  schedule)
    emit true "定时全量：每周跑一次，兜住路径过滤可能漏掉的改动"
    exit 0
    ;;
  workflow_dispatch)
    if [ "$FORCE" = "true" ]; then
      emit true "手动触发且勾选了强制"
    else
      emit false "手动触发未勾选 force_matrix"
    fi
    exit 0
    ;;
  push | pull_request) ;;
  *)
    emit true "未知事件类型「$EVENT」，保守全跑"
    exit 0
    ;;
esac

# 全 0 的 before 表示新建分支，没有可比较的基线
if [ -z "$BASE" ] || [ "${BASE//0/}" = "" ]; then
  emit true "没有可比较的基线提交（新建分支等），保守全跑"
  exit 0
fi

if ! git rev-parse --verify --quiet "$BASE^{commit}" > /dev/null; then
  emit true "基线提交在本地不存在，保守全跑"
  exit 0
fi

if [ -z "$HEAD" ] || ! git rev-parse --verify --quiet "$HEAD^{commit}" > /dev/null; then
  emit true "目标提交不可解析，保守全跑"
  exit 0
fi

CHANGED=$(git diff --name-only "$BASE" "$HEAD")
if [ -z "$CHANGED" ]; then
  emit true "没有检测到文件变化，保守全跑"
  exit 0
fi

HITS=$(printf '%s\n' "$CHANGED" | grep -E "$SENSITIVE" || true)
if [ -n "$HITS" ]; then
  emit true "命中检测力相关文件：$(printf '%s' "$HITS" | tr '\n' ' ')"
else
  emit false "本次改动不涉及检测力（共 $(printf '%s\n' "$CHANGED" | wc -l | tr -d ' ') 个文件）"
fi

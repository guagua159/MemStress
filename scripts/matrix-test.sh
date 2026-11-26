#!/usr/bin/env bash
# 63 格故障注入矩阵：7 模式 × 9 故障，断言每格都能检出错误。
#
# 为什么需要：这是检测力的回归。引擎、去重逻辑、注入路径任一改动都可能让某个
# 组合从「能检出」变成「静默不报」，而普通自检只跑默认模式，覆盖不到。
#
# 加速手段（2026-11-26 实测，总耗时 5.5 分钟 → 约 2.3 分钟）：
#   1. --skip-sysinfo：跳过 4 次 PowerShell/WMI 查询（约 2.1 秒/格，63 格共 2 分钟）。
#      矩阵只关心能否检出，不关心机器型号。单格 4.3s → 2.2s。
#   2. --seconds 2：注入的错误在第 1 秒内就出现，3 秒有冗余。
#      2 秒下 7 个模式 × 9 种故障各重复 5 次，检出率全部 5/5，未见不稳定。
#      注意不能降到 1 秒：ModuloX 在 1 秒下曾出现 0 检出（慢模式跑不完一轮）。
#   3. --percent 0.02：约 400 MB，分配更快且在 CI runner 上不与其他进程抢内存。
#
# 不采用并行：实测 4 个进程并行耗时 312 秒（比串行还慢），且有一格检出 0 条——
# 多实例抢内存会互相干扰结果。矩阵的结论必须可信，不能拿准确性换速度。
set -u

EXE=./MemStress.exe
PATTERNS="MovingInversion AddressAsData RandomFill BlockCopy WalkingOnes Checkerboard ModuloX"
FAULTS="ByteFlip StuckAt BitFlip BusLane AddressAlias Coupling RowStripe Burst Intermittent"
SECONDS_ARG=${MATRIX_SECONDS:-2}
PCT=${MATRIX_PERCENT:-0.02}

fail=0
total=0

for p in $PATTERNS; do
  for f in $FAULTS; do
    total=$((total + 1))
    out=$("$EXE" --selftest --seconds "$SECONDS_ARG" --percent "$PCT" --chunk 64 \
            --pattern "$p" --inject-error --inject-fault "$f" --skip-sysinfo 2>&1)
    rc=$?
    # 自检输出形如「内存中 64 条错误」；取不到即为 0
    n=$(echo "$out" | grep -oE "内存中 [0-9]+" | grep -oE "[0-9]+" | head -1)
    n=${n:-0}
    if [ "$rc" -ne 0 ] || [ "$n" -eq 0 ]; then
      echo "FAIL $p/$f exit=$rc errors=$n"
      echo "$out" | tail -5
      fail=$((fail + 1))
    fi
  done
done

if [ "$fail" -eq 0 ]; then
  echo "[matrix] PASS $total/$total"
  exit 0
fi
echo "[matrix] FAIL $fail/$total 格未检出"
exit 2

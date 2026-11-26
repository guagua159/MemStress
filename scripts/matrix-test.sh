#!/usr/bin/env bash
# 63 格故障注入矩阵：7 模式 × 9 故障，断言每格都能检出错误。
#
# 为什么需要：这是检测力的回归。引擎、去重逻辑、注入路径任一改动都可能让某个
# 组合从「能检出」变成「静默不报」，而普通自检只跑默认模式，覆盖不到。
#
# 耗时：本机实测单格约 5.5 秒（含进程启动），63 格约 6 分钟。
# 参数说明：--percent 0.04 是刻意压低（约 640 MB），避免在 CI runner 上与
# 其他进程抢内存；--seconds 3 足够跑完注入的故障。
set -u

EXE=./MemStress.exe
PATTERNS="MovingInversion AddressAsData RandomFill BlockCopy WalkingOnes Checkerboard ModuloX"
FAULTS="ByteFlip StuckAt BitFlip BusLane AddressAlias Coupling RowStripe Burst Intermittent"

fail=0
total=0

for p in $PATTERNS; do
  for f in $FAULTS; do
    total=$((total + 1))
    out=$("$EXE" --selftest --seconds 3 --percent 0.04 --chunk 64 \
            --pattern "$p" --inject-error --inject-fault "$f" 2>&1)
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

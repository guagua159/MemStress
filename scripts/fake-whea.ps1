# Test helper: emits JSON with the exact shape the real WHEA query returns,
# so WheaMonitor's parse path (query -> JSON -> parse -> filter -> log) can be
# verified on machines that have no real WHEA events. Timestamp is generated at
# runtime; a fixed one would be filtered out by the "only new events" rule.
# rid mimics EventRecordID: stable across repeated polls, so the dedup path that
# production relies on is exercised too.
$ts = (Get-Date).ToString('o')
$payload = [ordered]@{
    ok     = $true
    events = @(
        [ordered]@{ t = $ts; id = 47; level = 'Warning'; rid = 900001; msg = 'A corrected hardware error has occurred. Component: Memory Error Source: Corrected Machine Check' },
        [ordered]@{ t = $ts; id = 17; level = 'Warning'; rid = 900002; msg = 'A corrected hardware error has occurred. Component: PCI Express Root Port' }
    )
}
ConvertTo-Json -InputObject $payload -Compress -Depth 4

param([int]$Port = 8731, [int]$Count = 4)

# 用普通 WebSocket 客户端连后端，验证 IPC 推送是否正常
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ct = [System.Threading.CancellationToken]::None

try {
    $ws.ConnectAsync([Uri]("ws://127.0.0.1:$Port/"), $ct).Wait(8000)
} catch {
    Write-Output "连接失败: $($_.Exception.Message)"
    exit 2
}

Write-Output "已连接 ws://127.0.0.1:$Port/"

$hello = [System.Text.Encoding]::UTF8.GetBytes('{"type":"hello"}')
$null = $ws.SendAsync([ArraySegment[byte]]::new($hello),
    [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).Wait(5000)

$buf = New-Object byte[] 262144
for ($i = 0; $i -lt $Count; $i++) {
    $seg = [ArraySegment[byte]]::new($buf)
    $task = $ws.ReceiveAsync($seg, $ct)
    if (-not $task.Wait(9000)) { Write-Output "消息 $i 超时"; break }
    $res = $task.Result
    if ($res.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
        Write-Output "服务端关闭连接"; break
    }
    $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $res.Count)
    Write-Output "--- 消息 $i  ($($res.Count) 字节) ---"
    if ($text.Length -gt 1600) { $text = $text.Substring(0, 1600) + '  …(截断)' }
    Write-Output $text
    Write-Output ''
}

$ws.Dispose()

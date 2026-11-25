var retained = new List<byte[]>();
for (var i = 0; i < 128; i++) retained.Add(new byte[1024]);
Console.WriteLine($"Workload PID: {Environment.ProcessId}");
await Task.Delay(TimeSpan.FromSeconds(2));
for (var i = 0; i < 64; i++) retained.Add(new byte[1024]);
await Task.Delay(TimeSpan.FromSeconds(20));
Console.WriteLine($"Retained arrays: {retained.Count}");

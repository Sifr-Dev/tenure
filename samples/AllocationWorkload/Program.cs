var retained = new List<byte[]>();
for (var i = 0; i < 128; i++) retained.Add(new byte[1024]);
Console.WriteLine($"Workload PID: {Environment.ProcessId}");
await Task.Delay(TimeSpan.FromSeconds(10));
Console.WriteLine($"Retained arrays: {retained.Count}");

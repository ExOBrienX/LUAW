using BenchmarkDotNet.Running;

// Los benchmarks del tick se agregan cuando exista el motor (etapa 3).
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

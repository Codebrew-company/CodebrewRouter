using System.Reflection;
var asm = AssemblyName.GetAssemblyName("E:/src/CodebrewRouter/Blaze.LlmGateway.LocalInference/bin/Debug/net10.0/Blaze.LlmGateway.LocalInference.dll");
Console.WriteLine(asm.FullName);
var refs = Assembly.ReflectionOnlyLoadFrom(asm.CodeBase);
foreach (var r in refs.GetReferencedAssemblies())
    Console.WriteLine(r.FullName);

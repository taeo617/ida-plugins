// Dev loop without the .NET SDK: paste into Rhino 8 ScriptEditor (C#) or send via RhinoMCP.
// Compiles src/*.cs + dev/TestHarness.cs in memory with Rhino's own Roslyn and runs TestHarness.Run.
// Expects a `doc` (RhinoDoc) and `output` (StringBuilder) in scope; in ScriptEditor define them:
//   var doc = Rhino.RhinoDoc.ActiveDoc; var output = new System.Text.StringBuilder();
var ca = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Microsoft.CodeAnalysis");
var cs = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Microsoft.CodeAnalysis.CSharp");
var root = @"C:\Users\User\Documents\Rhino Agent\NPatch";
var files = System.IO.Directory.GetFiles(root + @"\src", "*.cs").Where(f => !f.EndsWith("AssemblyInfo.cs"))
  .Concat(new[] { root + @"\dev\TestHarness.cs" }).ToList();

object[] Defaults(System.Reflection.ParameterInfo[] ps) => ps.Select(p => p.HasDefaultValue ? p.DefaultValue
  : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray();

var parse = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree").GetMethods()
  .First(m => m.Name == "ParseText" && m.GetParameters()[0].ParameterType == typeof(string));
var trees = Array.CreateInstance(ca.GetType("Microsoft.CodeAnalysis.SyntaxTree"), files.Count);
for (int i = 0; i < files.Count; i++)
{
  var ps = parse.GetParameters(); var args = Defaults(ps);
  args[0] = System.IO.File.ReadAllText(files[i]);
  int pi = Array.FindIndex(ps, p => p.Name == "path"); if (pi >= 0) args[pi] = files[i];
  trees.SetValue(parse.Invoke(null, args), i);
}
var refType = ca.GetType("Microsoft.CodeAnalysis.MetadataReference");
var fromFile = refType.GetMethods().First(m => m.Name == "CreateFromFile");
// Skip an installed NPatch.rhp, or its types would clash with the freshly compiled ones.
var locs = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && a.GetName().Name != "NPatch").Select(a => a.Location).Distinct().ToList();
var refs = Array.CreateInstance(refType, locs.Count);
for (int i = 0; i < locs.Count; i++) { var a = Defaults(fromFile.GetParameters()); a[0] = locs[i]; refs.SetValue(fromFile.Invoke(null, a), i); }
var ctor = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions").GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
var cargs = Defaults(ctor.GetParameters());
cargs[0] = Enum.ToObject(ca.GetType("Microsoft.CodeAnalysis.OutputKind"), 2); // DynamicallyLinkedLibrary
int oi = Array.FindIndex(ctor.GetParameters(), p => p.Name == "optimizationLevel"); if (oi >= 0) cargs[oi] = Enum.ToObject(ctor.GetParameters()[oi].ParameterType, 1);
var create = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation").GetMethods().First(m => m.Name == "Create" && m.GetParameters().Length == 4);
var comp = create.Invoke(null, new object[] { "NPatchDev" + Guid.NewGuid().ToString("N"), trees, refs, ctor.Invoke(cargs) });
var emit = comp.GetType().GetMethods().First(m => m.Name == "Emit" && m.GetParameters()[0].ParameterType == typeof(System.IO.Stream));
var ms = new System.IO.MemoryStream();
var eargs = Defaults(emit.GetParameters()); eargs[0] = ms;
var result = emit.Invoke(comp, eargs);
if (!(bool)result.GetType().GetProperty("Success").GetValue(result))
{
  foreach (var d in (System.Collections.IEnumerable)result.GetType().GetProperty("Diagnostics").GetValue(result))
    if (d.ToString().Contains("error")) output.AppendLine(d.ToString());
}
else
{
  System.Reflection.Assembly.Load(ms.ToArray()).GetType("NPatch.Dev.TestHarness").GetMethod("Run").Invoke(null, new object[] { doc, output });
}

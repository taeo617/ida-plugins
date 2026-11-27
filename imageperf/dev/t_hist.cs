// Dumps the last part of the command history to dev\out\log.txt.
using System.IO;
using Rhino;
var h = RhinoApp.CommandHistoryWindowText ?? "";
if (h.Length > 4000) h = h.Substring(h.Length - 4000);
File.WriteAllText(@"C:\Users\User\Documents\Rhino Agent\ida-plugins\imageperf\dev\out\log.txt", "HISTORY\n" + h + "\nPROMPT: " + RhinoApp.CommandPrompt);

using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

class P {
  static void Main(string[] a){
    string path=a[0];
    var asm=AssemblyDefinition.ReadAssembly(path, new ReaderParameters{ });
    foreach(var mod in asm.Modules){
      foreach(var t in mod.Types){
        if(!t.Name.Contains("US2Dome") && !t.Name.Contains("DomeWorker") && !t.Name.Contains("DomeRemap") && !t.Name.Contains("DomeWindow")) continue;
        Console.WriteLine("== TYPE "+t.FullName);
        foreach(var f in t.Fields){ Console.WriteLine("   field "+f.FullName); }
        foreach(var m in t.Methods){
          Console.WriteLine("   method "+m.FullName + " (IL len "+(m.HasBody?m.Body.Instructions.Count.ToString()+", maxstack "+m.Body.MaxStackSize.ToString():"")+")");
          if((m.Name=="ReadFaces"||m.Name=="ConsumeWorkerResult"||m.Name=="Loop"||m.Name=="TryGetResult"||m.Name=="RemapBytes"||m.Name=="WindowResample"||m.Name=="Update"||m.Name=="OnGUI"||m.Name=="Submit"||m.Name=="PushBytes"||m.Name=="Awake"||m.Name=="EnsureInitialized")){
            Console.WriteLine("   ---- BODY of "+m.Name+" ----");
            var wr=new StringWriter();
            new ILWriter().Dump(m, wr);
            Console.Write(wr.ToString());
          }
        }
      }
    }
  }
}
class ILWriter{
  public void Dump(MethodDefinition m, System.IO.StringWriter sw){
    foreach(var i in m.Body.Instructions){
      sw.WriteLine("      "+i.OpCode.Name);
      if(i.Operand!=null){
        var o=i.Operand;
        if(o is MethodReference mm) sw.WriteLine("            -> "+mm.FullName);
        else if(o is FieldReference ff) sw.WriteLine("            -> "+ff.FullName);
        else if(o is TypeReference tt) sw.WriteLine("            -> "+tt.FullName);
        else sw.WriteLine("            -> "+o);
      }
    }
  }
}

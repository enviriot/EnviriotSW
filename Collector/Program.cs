///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace X13 {
  internal class Program {
    private static Queue<string> _dirs;
    static void Main(string[] args) {
      _dirs = new Queue<string>();
      _dirs.Enqueue("catalog");
      while(_dirs.Count > 0) {
        ParseDir(_dirs.Dequeue());
      }

      Console.ReadLine();
    }
    private static void ParseDir(string path) {
      Console.WriteLine("  {0}", path);
      string indexFn = path + "/index.json", dirName, json;
      List<JSC.JSValue> lstN = new List<JSC.JSValue>();
      JSC.JSObject jc;
      SortedList<string, JSC.JSValue> olds = new SortedList<string, JSC.JSValue>();

      if(File.Exists(indexFn)) {
        json = File.ReadAllText(indexFn, Encoding.UTF8);
        var jo = JSL.JSON.parse(json);
        foreach(var js in jo) {
          if(js.Value["children"].ValueType == JSC.JSValueType.String && (dirName = js.Value["children"].Value as string)!=null) {
            olds.Add(dirName, js.Value);
          }
        }
      }
      foreach(var dp in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)) {
        _dirs.Enqueue(dp);
        dirName = dp.Substring(path.Length + 1);
        if(olds.ContainsKey(dirName)) {
          lstN.Add(olds[dirName]);
        } else {
          jc = JSC.JSObject.CreateObject();
          jc["name"] = dirName;
          jc["hint"] = string.Empty;
          jc["children"] = dirName;
          lstN.Add(jc);
        }
      }
      foreach(var fn in Directory.EnumerateFiles(path, "*.xst", SearchOption.TopDirectoryOnly)) {
        jc = Import(fn);
        if(jc != null) {
          lstN.Add(jc);
        }
      }

      var arr = new JSL.Array(lstN.Count);
      for(int i = 0; i<lstN.Count; i++) {
        arr[i] = lstN[i];
      }
      json = JSL.JSON.stringify(arr, null, null, "");
      File.WriteAllText(indexFn, json, Encoding.UTF8);
    }

    private static JSC.JSObject Import(string fn) {
      Console.WriteLine("    {0}", fn);
      var jo = JSC.JSObject.CreateObject();
      XDocument doc = XDocument.Load(fn);
      XElement el = doc.Root.Element("i");
      if(el == null || el.Attribute("n") == null) {
        return null;
      }
      string path = doc.Root.Attribute("path").Value;
      jo["name"] = el.Attribute("n").Value;
      jo["src"] = Path.GetFileName(fn);
      jo["path"] =path + "/" + el.Attribute("n").Value;
      Version ver;
      if(el.Attribute("ver") != null && Version.TryParse(el.Attribute("ver").Value, out ver)) {
        jo["ver"] = ver.ToString(4);
      }
      if(el.Attribute("s") != null) {
        try {
          var st = JSL.JSON.parse(el.Attribute("s").Value);
          JSC.JSValue hj;
          string hint;
          if(st!=null && (hj=st["hint"])!=null && hj.ValueType==JSC.JSValueType.String && (hint = hj.Value as string)!=null) {
            jo["hint"] = hint;
          }
        }
        catch(Exception ex) {
          Console.WriteLine("E   {0}.s - {1}",fn , ex.Message);
        }
      }

      //"hint":"",

      return jo;
    }
  }
}

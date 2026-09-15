///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>using NiL.JS.Core;
using NiL.JS.Core;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace X13.Repository {
  /// <summary>Импорт и экспорт поддерева репозитория в формате .xst.</summary>
  /// <remarks>Вызывается из четырёх независимых мест: при загрузке начальной конфигурации, 
  /// применении значений по умолчанию плагина хранилища, установке пакетов из каталога и загрузке или скачивании через WebUI.
  /// <para>Метаданные передаются в JSON-кодировке в атрибуте "m", состояние — в атрибуте "s",
  /// а "ver" служит ограничителем импорта: элемент, версия которого не новее уже имеющейся
  /// в дереве, полностью пропускается.</para></remarks>
  public static class Xst {
    public static bool Import(string fileName, string path = null) {
      if(string.IsNullOrEmpty(fileName) || !File.Exists(fileName)) {
        return false;
      }
      X13.Log.Info("Import {0}", Path.GetFullPath(fileName));
      using(StreamReader reader = File.OpenText(fileName)) {
        Import(reader, path);
      }
      return true;
    }

    /// <summary>Читает и применяет документ целиком либо не применяет его вовсе.</summary>
    /// <remarks>Обработка выполняется в два прохода. Первый считывает XML в план и проверяет каждое имя,
    /// запрошенное документом; второй создаёт топики. До полного чтения документа ничего не создаётся,
    /// поэтому документ, который невозможно применить, оставляет дерево без изменений.
    /// <para>При первом проходе НЕ отклоняются некорректные атрибуты "m" или "s": как и раньше,
    /// записывается предупреждение, а узел создаётся без такого значения.</para></remarks>
    public static void Import(TextReader reader, string path) {
      XDocument doc;
      using(var r = new System.Xml.XmlTextReader(reader)) {
        doc = XDocument.Load(r);
      }
      if(string.IsNullOrEmpty(path) && doc.Root.Attribute("path") != null) {
        path = doc.Root.Attribute("path").Value;
      }
      if(string.IsNullOrEmpty(path)) {
        return;   // не указан путь, по которому следует применить документ
      }
      Topic.CheckPath(path, "Import");
      Topic existing;
      Topic.root.Exist(path, out existing);
      Node plan = Prepare(doc.Root, existing, null);
      if(plan != null) {   // null означает, что документ не новее уже имеющихся данных
        Apply(plan, null, path);
      }
    }

    /// <summary>Один запрошенный документом узел: прочитан и проверен, но ещё не создан.</summary>
    private sealed class Node {
      public string Name;          // для корня null, поскольку он задаётся путём
      public JSValue State;
      public JSValue Manifest;
      public readonly List<Node> Children = new List<Node>();
    }

    /// <summary>Считывает элемент и его поддерево в план. Возвращает null, если документ пропускает элемент.</summary>
    /// <param name="existing">Существующий топик, которому соответствует этот элемент. Требуется только для проверки версии;
    /// для всего создаваемого поддерева равен null.</param>
    private static Node Prepare(XElement x, Topic existing, string name) {
      Version ver;
      bool setVersion;
      if(x.Attribute("ver") != null && Version.TryParse(x.Attribute("ver").Value, out ver)) {
        if(existing != null) {
          Version oldVer;
          var ov_js = existing.GetField("version");
          string ov_s;
          if(ov_js.Is<string>() && (ov_s = ov_js.Value as string) != null && ov_s.StartsWith("¤VR")
              && Version.TryParse(ov_s.Substring(3), out oldVer) && oldVer >= ver) {
            return null;   // не импортируем более старую версию
          }
        }
        setVersion = true;
      } else {
        ver = default(Version);
        setVersion = false;
      }
      Node node = new Node { Name = name };
      if(x.Attribute("m") != null) {
        try {
          node.Manifest = JsLib.ParseJson(x.Attribute("m").Value);
        }
        catch(Exception ex) {
          Log.Warning("Import({0}).m - {1}", x.ToString(), ex.Message);
        }
      }
      if(setVersion) {
        node.Manifest = JsLib.SetField(node.Manifest, "version", "¤VR" + ver.ToString());
      }
      if(x.Attribute("s") != null) {
        try {
          node.State = JsLib.ParseJson(x.Attribute("s").Value);
        }
        catch(Exception ex) {
          Log.Warning("Import({0}).s - {1}", x.ToString(), ex.Message);
        }
      }
      foreach(var xNext in x.Elements("i")) {
        XAttribute n = xNext.Attribute("n");
        if(n == null) {
          continue;   // элемент не имеет адреса и никогда его не имел
        }
        Topic.CheckName(n.Value, "Import");
        Topic child = null;
        if(existing != null) {
          existing.Exist(n.Value, out child);
        }
        Node next = Prepare(xNext, child, n.Value);
        if(next != null) {
          node.Children.Add(next);
        }
      }
      return node;
    }

    private static void Apply(Node node, Topic owner, string path) {
      Topic cur = owner == null ? Topic.Declare(Topic.root, path) : Topic.Declare(owner, node.Name);
      Topic.Fill(cur, node.State, node.Manifest, null);
      for(int i = 0; i < node.Children.Count; i++) {
        Apply(node.Children[i], cur, null);
      }
    }
    /// <summary>Записывает дерево в файл, заменяя прежний файл только после завершения записи нового.</summary>
    public static void Export(string filename, Topic t, bool configOnly) {
      if(filename == null) {
        throw new ArgumentNullException("filename");
      }
      string tmp = filename + ".tmp";
      try {
        using(FileStream stream = File.Create(tmp)) {
          Export(stream, t, configOnly);
        }
        Swap(tmp, filename);
      }
      catch {
        try {
          File.Delete(tmp);   // не выбрасывает исключение, если файл не был создан
        }
        catch(Exception ex) {
          Log.Warning("Export({0}) - {1} could not be removed: {2}", filename, tmp, ex.Message);
        }
        throw;
      }
    }

    /// <summary>Устанавливает только что записанный файл вместо заменяемого.</summary>
    private static void Swap(string tmp, string filename) {
      if(File.Exists(filename)) {
        File.Replace(tmp, filename, null);
      } else {
        File.Move(tmp, filename);
      }
    }

    public static void Export(Stream stream, Topic t, bool configOnly) {
      if(stream == null) {
        throw new ArgumentNullException("stream");
       }
      XDocument doc = BuildExportDocument(t, configOnly);
      System.Xml.XmlTextWriter writer = new System.Xml.XmlTextWriter(stream, Encoding.UTF8);
      writer.Formatting = System.Xml.Formatting.Indented;
      writer.QuoteChar = '\'';
      doc.WriteTo(writer);
      writer.Flush();
    }
    private static XDocument BuildExportDocument(Topic t, bool configOnly) {
      if(t == null) {
        throw new ArgumentNullException("topic");
      }
      XDocument doc = new XDocument(new XElement("xst", new XAttribute("path", t.path)));
      doc.Declaration = new XDeclaration("1.0", "utf-8", "yes");
      var s = t.GetState();
      if(s.Exists && (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config) || (!configOnly && t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)))) {
        doc.Root.Add(new XAttribute("s", JsLib.Stringify(s)));
      }
      var m = t.GetField(null);
      doc.Root.Add(new XAttribute("m", JsLib.Stringify(m)));
      foreach(Topic c in t.children) {
        Export(doc.Root, c, configOnly);
      }
      return doc;
    }
    private static void Export(XElement x, Topic t, bool configOnly) {
      if(x == null || t == null) {
        return;
      }
      XElement xCur = new XElement("i", new XAttribute("n", t.name));
      foreach(Topic c in t.children) {
        Export(xCur, c, configOnly);
      }
      if(!configOnly || xCur.HasElements || t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config)) {
        var s = t.GetState();
        if(s.Exists && (t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config) || (!configOnly && t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB)))) {
          var state_json = JsLib.Stringify(s);
          if(state_json!=null) {
            xCur.Add(new XAttribute("s", state_json));
          }
        }

        var m = t.GetField(null);
        var manifest_json = JsLib.Stringify(m);
        if(manifest_json!=null){
          xCur.Add(new XAttribute("m", manifest_json));
        }

        x.Add(xCur);
      }
    }
  }
}

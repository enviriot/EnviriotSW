///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  /// <summary>Наложение источников схемы, общее для деревьев State и Manifest.</summary>
  /// <remarks>Источники упорядочены от низшего приоритета к высшему; каждый следующий дополняет
  /// предыдущий и побеждает при совпадении ключей. Правило действует на каждом уровне спуска, а
  /// не только на верхнем: спускаясь в поле, наложение спускается сразу по всем источникам,
  /// у которых этот ключ есть.
  /// <para>Это <c>DTopic.ProtoDeep</c> из ES, развёрнутый в явный список. Там наложение делала
  /// цепочка прототипов JS: манифест топика рекурсивно сшивался с состоянием топика типа
  /// (<c>m.__proto__ = p</c> по всем собственным ключам вглубь), после чего <c>_manifest["Fields"]</c>
  /// и <c>_manifest["editor"]</c> читались обычными обращениями к свойству и наследование
  /// получалось само. Повторять это на сервере нельзя: <c>ProtoDeep</c> мутирует живые объекты, а в
  /// ES они приватны для соединения (<c>DTopic</c> — копия), тогда как здесь состояние топика типа и
  /// глобальная схема общие для всех сессий. Назначение <c>__proto__</c> тут уже приводило к тому,
  /// что резолв одного топика оставлял свой манифест следующему.</para>
  /// <para>Различие между двумя панелями сведено к двум параметрам: ключ каталога
  /// (<c>Fields</c> у State, <c>mi</c> у Manifest) и набор источников — см. <see cref="ForState"/>
  /// и <see cref="ForManifest"/>.</para></remarks>
  internal sealed class SchemaOverlay {
    private const string ManifestSchemaTopicPath = "/$YS/TYPES/Ext/Manifest";
    private const string CoreTypesTopicPath = "/$YS/TYPES/Core";

    /// <summary>Один слой наложения.</summary>
    /// <remarks><see cref="CarriesDescriptor"/> различает две роли, которые обычно совпадают:
    /// источник отдаёт каталог и, кроме того, собственные ключи как свойства дескриптора. Ложно
    /// он ровно в одном месте - собственный манифест топика на корне дерева Manifest, где
    /// манифест является редактируемым значением, а не дескриптором самого себя. Так же устроен
    /// и ES: <c>InManifest.UpdateType</c> берёт каталог из <c>IsGroupHeader ? _value : _manifest</c>,
    /// а <c>editor</c>/<c>icon</c>/<c>attr</c> корня читает только из глобальной схемы.</remarks>
    private struct Source {
      internal JSC.JSValue Value;
      internal bool CarriesDescriptor;
    }

    private readonly string _catalogKey;
    private readonly List<Source> _sources;

    private SchemaOverlay(string catalogKey, List<Source> sources) {
      _catalogKey = catalogKey;
      _sources = sources;
    }

    /// <summary>Схема состояния топика: каталог типа, поверх - собственный манифест.</summary>
    /// <remarks><c>type</c> разрешается один раз, здесь, а не на каждом сегменте пути - как
    /// <c>ProtoDeep</c>, который вызывается однократно на топик и дальше только рекурсирует.
    /// Прежняя реализация читала <c>type</c> у каждого вложенного дескриптора, что не только
    /// ничего не находило, но и в принципе неверно: запись <c>Fields.type</c> - это дескриптор
    /// поля с именем <c>type</c> (см. <c>Ext/LBDescr</c> в base.xst), а не путь к топику типа.</remarks>
    internal static SchemaOverlay ForState(Topic topic) {
      List<Source> sources = new List<Source>(2);
      JSC.JSValue manifest = topic == null ? null : topic.GetField(null);
      AddSource(sources, TypeState(manifest), true);
      AddSource(sources, manifest, true);
      return new SchemaOverlay("Fields", sources);
    }

    /// <summary>Схема манифеста топика: глобальная схема, каталог типа, собственный манифест.</summary>
    internal static SchemaOverlay ForManifest(Topic topic) {
      List<Source> sources = new List<Source>(3);
      Topic schemaTopic = Topic.root.Get(ManifestSchemaTopicPath, false);
      AddSource(sources, schemaTopic == null ? null : schemaTopic.GetState(), true);
      JSC.JSValue manifest = topic == null ? null : topic.GetField(null);
      AddSource(sources, TypeState(manifest), true);
      // The topic's own manifest at the root is the value this tree shows and edits, so its keys
      // (attr, type, MQTT, ...) are not descriptor properties - see Source.CarriesDescriptor.
      // At any nested level the source becomes mi.<segment>, a real descriptor, and the flag is
      // true again there (see Descend).
      AddSource(sources, manifest, false);
      return new SchemaOverlay("mi", sources);
    }

    private static JSC.JSValue TypeState(JSC.JSValue manifest) {
      Topic typeTopic = TypeHelper.ResolveTypeTopic(manifest.AsString("type", null));
      return typeTopic == null ? null : typeTopic.GetState();
    }

    // IsObject, not ValueType == Object: the latter is TRUE for JSValue.Null, whose null sits in
    // Value, and the indexer reads below would then throw.
    private static void AddSource(List<Source> sources, JSC.JSValue value, bool carriesDescriptor) {
      if(value.IsObject()) sources.Add(new Source() { Value = value, CarriesDescriptor = carriesDescriptor });
    }

    /// <summary>Наложение на узле <paramref name="fieldPath"/>; null, когда путь не объявлен ни одним источником.</summary>
    internal SchemaOverlay At(string fieldPath) {
      if(string.IsNullOrEmpty(fieldPath)) return this;
      SchemaOverlay current = this;
      foreach(string segment in fieldPath.Split(JsLib.SPLITTER_OBJ, StringSplitOptions.RemoveEmptyEntries)) {
        current = current.Descend(segment);
        if(current == null) return null;
      }
      return current;
    }

    // Descends the source trees, not the merged descriptor: the merge drops the catalog key, so
    // the result carries nothing to walk into.
    private SchemaOverlay Descend(string segment) {
      List<Source> next = new List<Source>(_sources.Count);
      foreach(Source source in _sources) {
        JSC.JSValue catalog = source.Value.Field(_catalogKey);
        if(!catalog.IsObject()) continue;
        AddSource(next, catalog[segment], true);
      }
      return next.Count == 0 ? null : new SchemaOverlay(_catalogKey, next);
    }

    /// <summary>Свойства дескриптора всех источников, наложенные по ключам; ключ каталога исключён.</summary>
    /// <remarks>Каждый раз новый объект: живые объекты схемы принадлежат репозиторию и общие для
    /// всех сессий, поэтому ни один из них не мутируется и <c>__proto__</c> никому не назначается.
    /// Ключ каталога отброшен намеренно - каталог отдаёт <see cref="CatalogEntries"/>, уже
    /// объединённый по всем источникам, тогда как оставленный в дескрипторе он был бы каталогом
    /// одного источника.</remarks>
    internal JSC.JSValue Descriptor {
      get {
        JSC.JSObject merged = JSC.JSObject.CreateObject();
        foreach(Source source in _sources) {
          if(!source.CarriesDescriptor) continue;
          foreach(var kv in source.Value) {
            if(kv.Key != _catalogKey) merged[kv.Key] = kv.Value;
          }
        }
        return merged;
      }
    }

    /// <summary>Объединение каталогов всех источников, по возрастанию ключа.</summary>
    /// <remarks>Записи с одинаковым ключом сливаются по свойствам, а не заменяются целиком:
    /// частичная запись верхнего слоя иначе спрятала бы <c>default</c> нижнего, а по нему
    /// фильтруется меню Add - и пункт бы исчез.</remarks>
    internal IEnumerable<KeyValuePair<string, JSC.JSValue>> CatalogEntries {
      get { return BuildCatalog().OrderBy(z => z.Key, StringComparer.Ordinal); }
    }

    internal JSC.JSValue CatalogEntry(string key) {
      JSC.JSValue entry;
      return BuildCatalog().TryGetValue(key, out entry) ? entry : null;
    }

    internal bool CatalogIsEmpty {
      get { return BuildCatalog().Count == 0; }
    }

    private Dictionary<string, JSC.JSValue> BuildCatalog() {
      Dictionary<string, JSC.JSValue> catalog = new Dictionary<string, JSC.JSValue>(StringComparer.Ordinal);
      foreach(Source source in _sources) {
        JSC.JSValue entries = source.Value.Field(_catalogKey);
        if(!entries.IsObject()) continue;
        foreach(var kv in entries) {
          if(!kv.Value.IsObject()) continue;
          JSC.JSValue lower;
          catalog[kv.Key] = catalog.TryGetValue(kv.Key, out lower) ? MergeEntry(lower, kv.Value) : kv.Value;
        }
      }
      return catalog;
    }

    // The lower entry with the upper one laid over it - a fresh object, for the same reason
    // Descriptor is one. The catalog key is carried through as any other: whoever descends into
    // this entry goes through Descend, which reads the sources rather than this result.
    private static JSC.JSValue MergeEntry(JSC.JSValue lower, JSC.JSValue upper) {
      JSC.JSObject merged = JSC.JSObject.CreateObject();
      foreach(var kv in lower) merged[kv.Key] = kv.Value;
      foreach(var kv in upper) merged[kv.Key] = kv.Value;
      return merged;
    }

    /// <summary>Каталог /$YS/TYPES/Core - запасной, когда схемы для узла нет вовсе.</summary>
    /// <remarks>Дочерние топики, их состояние и есть дескрипторы. Уровень меню Add, а не резолва
    /// дескриптора строки: так же в ES (<c>InValue.MenuItems</c> и <c>InManifest.MenuItems</c>
    /// обращаются к <c>Connection.CoreTypes.children</c> только в ветке "каталога нет"), и иначе
    /// поле, случайно названное String, подхватывало бы иконку и editor из Core/String.</remarks>
    internal static IEnumerable<KeyValuePair<string, JSC.JSValue>> CoreCatalogEntries() {
      Topic coreTypes = Topic.root.Get(CoreTypesTopicPath, false);
      if(coreTypes == null) return Enumerable.Empty<KeyValuePair<string, JSC.JSValue>>();
      return coreTypes.children
        .OrderBy(z => z.name, StringComparer.Ordinal)
        .Select(z => new KeyValuePair<string, JSC.JSValue>(z.name, z.GetState()));
    }

    internal static JSC.JSValue CoreCatalogEntry(string key) {
      Topic coreTypes = Topic.root.Get(CoreTypesTopicPath, false);
      Topic coreType = coreTypes == null ? null : coreTypes.Get(key, false);
      return coreType == null ? null : coreType.GetState();
    }
  }
}

///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;
using NiL.JS.Extensions;
using System;
using System.Collections.Generic;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Repository {
  /// <summary>Узел дерева: путь, состояние, манифест и атрибуты.</summary>
  /// <remarks>Структура меняется сразу и на потоке вызвавшего, а состояние, манифест и все события откладываются до тика репозитория.</remarks>
  public sealed class Topic : IComparable<Topic> {

    /// <summary>Синхронизирует структурные изменения: создание, отсоединение и перемещение узлов.</summary>
    private static readonly object _structural = new object();
    private static readonly KeyValuePair<string, Topic>[] NoChildren = new KeyValuePair<string, Topic>[0];
    private static readonly SubRec[] NoSubs = new SubRec[0];
    private static Repo _repo;
    public static Topic root { get; private set; }

    internal static void Init(Repo repo) {
      Topic._repo = repo;
      Topic.root = new Topic(null, "/", false) {
        _manifest = JSObject.CreateObject()
      };
      Topic.root._manifest["attr"] = new JSL.Number((int)(Attribute.Required | Attribute.Internal));
    }

    #region Member variables
    private readonly object _sync;
    private Topic _parent;
    private string _name;
    private string _path;
    /// <summary>Узлы отсортированы в порядке имён Ordinal. Массив неизменяемый: заменяется целиком, но не изменяется на месте.</summary>
    private volatile KeyValuePair<string, Topic>[] _children = NoChildren;
    /// <summary>Подписки, созданные на этом топике. Массив неизменяемый.</summary>
    private volatile SubRec[] _subRecords = NoSubs;
    private JSC.JSValue _state;
    private JSC.JSValue _manifest;
    private FieldBatch _mfst_pu;

    /// <summary>Манифест, формируемый тиком для одного топика и общий для всех операций записи в него.</summary>
    /// <remarks>Объект используется совместно, чтобы топик получал один новый манифест, а не частичный манифест
    /// для каждой записи, и чтобы каждое событие пакета сообщало один и тот же манифест, существовавший до пакета.
    /// Неважно, какая запись выполнит замену: это делает первая применённая запись, остальные считывают результат
    /// отсюда.</remarks>
    internal sealed class FieldBatch {
      public JSC.JSValue value;         // формируемый манифест
      public JSC.JSValue oldManifest;   // манифест до пакета; заполняется при замене
      public bool swapped;
    }

    #endregion Member variables

    private Topic(Topic parent, string name, bool fill) {
      _sync = new object();
      _name = name;
      _parent = parent;
      _state = JSC.JSValue.Undefined;
      disposed = false;
      if (parent == null) {
        _path = "/";
      } else if (parent == root) {
        _path = "/" + name;
      } else {
        _path = parent._path + "/" + name;
      }
      if (fill) {
        _manifest = JSC.JSObject.CreateObject();
        _manifest["attr"] = new JSL.Number((int)0);
      }
    }

    public Topic parent {
      get { return _parent; }
      internal set { _parent = value; }
    }
    public string name {
      get { return _name; }
    }
    public string path { get { return _path; } }
    public bool disposed { get; private set; }
    public Bill all { get { return new Bill(this, true); } }
    public Bill children { get { return new Bill(this, false); } }
    public bool HasChildren() {
      KeyValuePair<string, Topic>[] kids = _children;
      for (int i = 0; i < kids.Length; i++) {
        if (!kids[i].Value.disposed) return true;
      }
      return false;
    }
    /// <summary> Получает элемент из дерева</summary>
    /// <param name="path">относительный или абсолютный путь</param>
    /// <param name="create">true - создать, false - только проверить</param>
    /// <returns>элемент или null</returns>
    /// <summary>Находит топик по пути и, если не указано обратное, создаёт отсутствующие узлы.</summary>
    /// <remarks>Ниже описано правило, которому следует весь структурный API. Оно приведено здесь,
    /// поскольку большинство вызывающих обращается к структуре через Get:
    /// <list type="bullet">
    /// <item>ЗАПРОС возвращает ответ, а не выбрасывает исключение: Get(create: false) и Exist возвращают null
    /// или false для отсутствующего топика, GetField возвращает Undefined, а методы Try- возвращают bool.
    /// null является результатом, а не ошибкой.</item>
    /// <item>КОМАНДА отклоняет недопустимый аргумент с помощью ArgumentException, указывая проблемное значение:
    /// Get(create: true), Declare, Move, SetField, Import. ArgumentNullException используется в обычном для .NET
    /// значении: ссылочный аргумент равен null.</item>
    /// <item>КОМАНДА, которую невозможно выполнить в текущем состоянии дерева, выбрасывает
    /// InvalidOperationException, например при перемещении корня или удалённого топика.</item>
    /// <item>Команда НИКОГДА не должна молча ничего не делать. Молчание неотличимо от успеха, поэтому
    /// проигнорированный вызывающий код продолжает считать, что дерево изменилось.</item>
    /// </list>
    /// Получивший имя извне код сначала вызывает IsValidName, а не перехватывает исключение.</remarks>
    public Topic Get(string path, bool create = true, Topic prim = null) {
      return Resolve(this, path, create, prim, true);
    }
    public bool Exist(string path) {
      return Resolve(this, path, false, null, false) != null;
    }
    public bool Exist(string path, out Topic topic) {
      return (topic = Resolve(this, path, false, null, false)) != null;
    }
    /// <summary>Перемещает топик к другому родителю, переименовывает его либо выполняет оба действия.</summary>
    /// <remarks>Всё структурное изменение выполняется под одной блокировкой и в порядке, при котором топик
    /// находится либо в одном месте, либо ни в одном, но никогда в двух.</remarks>
    public void Move(Topic nParent, string nName, Topic prim = null) {
      if (this._parent == null) {
        // Операция отклоняется, а не игнорируется. 
        throw new InvalidOperationException(this._path + ".Move - the root cannot be moved");
      }
      if (this.disposed) {
        throw new InvalidOperationException(this._path + ".Move - the topic has been removed");
      }
      if (nParent == null) {
        nParent = this.parent;
      }
      if (string.IsNullOrEmpty(nName)) {
        nName = this.name;
      }
      CheckName(nName, this._path + ".Move");
      string oldPath;
      // Запоминается под той же блокировкой, что и сам перенос: публикация идёт тиком позже,
      // и к ней прежнее место известно только отсюда.
      Topic oldParentOfMoved;
      lock (_structural) {
        // Топик не может стать собственным потомком.
        for (Topic p = nParent; p != null; p = p._parent) {
          if (p == this) {
            throw new ArgumentException(this._path + ".Move(" + nParent._path + ", " + nName + ") - a topic cannot be moved inside itself");
          }
        }
        KeyValuePair<string, Topic>[] target = nParent._children;
        int to = IndexOf(target, nName);
        if (to >= 0 && target[to].Value != this) {
          throw new ArgumentException(this._path + ".Move(" + nParent._path + ", " + nName + ") - the name is taken");
        }
        Topic oldParent = this._parent;
        oldParentOfMoved = oldParent;
        string oldName = this._name;
        oldPath = this._path;
        KeyValuePair<string, Topic>[] source = oldParent._children;
        int from = IndexOf(source, oldName);
        if (from < 0 || source[from].Value != this) {
          throw new InvalidOperationException(this._path + ".Move(" + nParent._path + ", " + nName + ") - not registered under its own parent");
        }
        oldParent._children = RemovedAt(source, from);
        _parent = nParent;
        _name = nName;
        UpdatePath(this);
        // Повторно читаем целевой массив: снимок выше получен до удаления, а при переименовании внутри
        // одного родителя это именно тот массив, который был заменён удалением.
        target = nParent._children;
        to = IndexOf(target, nName);
        if (to >= 0) {
          // Ветка недостижима, пока все структурные изменения выполняются под этой блокировкой.
          // Изменения откатываются, а не только проверяются, поскольку иначе топик не останется ни в одной ветви.
          _parent = oldParent;
          _name = oldName;
          UpdatePath(this);
          source = oldParent._children;
          oldParent._children = Inserted(source, ~IndexOf(source, oldName), oldName, this);
          throw new InvalidOperationException(oldPath + ".Move(" + nParent._path + ", " + nName + ") - the name was taken under the lock");
        }
        nParent._children = Inserted(target, ~to, nName, this);
      }
      _repo.DoCmd(new CmdMove(this, oldPath, oldParentOfMoved, prim));
    }
    /// <summary>Немедленно помечает топик удалённым; отсоединение и событие выполняются в следующем тике.</summary>
    /// <remarks>Флаг disposed намеренно устанавливается здесь, а не в CmdRemove.Apply. Это соответствует
    /// общему правилу структуры: создание и перемещение также вступают в силу в потоке вызывающего кода,
    /// а откладываются только события. Bill и HasChildren читают этот флаг, поэтому удалённое поддерево
    /// перестаёт перечисляться сразу, а не через тик.</remarks>
    public void Remove(Topic prim = null) {
      // Повторный вызов - не ошибка и не работа: задача выполнена, пусть и раньше.
      lock (_structural) {
        if (this.disposed) {
          return;
        }
        this.disposed = true;
      }
      var c = new CmdRemove(this, prim);
      _repo.DoCmd(c);
    }
    public SubRec Subscribe(SubRec.SubMask mask, Action<TopicEvent, SubRec> func) {
      return Subscribe(mask, null, func);
    }
    public SubRec Subscribe(SubRec.SubMask mask, string prefix, Action<TopicEvent, SubRec> func) {
      if (func == null) {
        throw new ArgumentNullException(this.path + ".Subscribe(func == NULL, " + mask.ToString() + (prefix == null ? string.Empty : ", " + prefix) + ")");
      }
      // Префикс приводится к единому виду здесь, до поиска: при бите Field null и пустая строка
      // одинаково означают "любое поле".
      if (prefix == null && (mask & SubRec.SubMask.Field) == SubRec.SubMask.Field) {
        prefix = string.Empty;
      }
      SubRec sb;
      bool exist;
      lock (_sync) {
        sb = Find(_subRecords, func, mask, prefix);
        exist = sb != null;
        if (!exist) {
          sb = new SubRec(this, func, mask, prefix);
          SubRec[] old = _subRecords;
          SubRec[] next = new SubRec[old.Length + 1];
          Array.Copy(old, next, old.Length);
          next[old.Length] = sb;
          _subRecords = next;
        }
      }
      // Если подписка уже существовала, используется subAck, а не subscribe: вызывающий код получает ответ,
      // но снимок не отправляется повторно для непрерывавшейся подписки.
      Cmd c = exist ? (Cmd)new CmdAck(this, sb) : new CmdSubscribe(this, sb);
      _repo.DoCmd(c);
      return sb;
    }

    public JSValue GetState() {
      return _state ?? JSValue.Null;
    }
    public void SetState(JSValue val, Topic prim = null) {
      _repo.DoCmd(new CmdState(this, val, prim));
    }

    public JSValue GetField(string fPath) {
      if (_manifest == null) {
        return JSValue.Undefined;
      }
      if (string.IsNullOrEmpty(fPath)) {
        return _manifest;
      }
      var ps = fPath.Split(Bill.delmiterObj, StringSplitOptions.RemoveEmptyEntries);
      JSValue val = _manifest;
      for (int i = 0; i < ps.Length; i++) {
        if (!val.IsObject()) return JSValue.Undefined;
        val = val.GetProperty(ps[i]);
      }
      return val;
    }
    public bool TrySetField(string fPath, JSValue value, Topic prim) {
      if (string.IsNullOrEmpty(fPath)) return false;
      _repo.DoCmd(new CmdField(this, fPath, value, prim));
      return true;
    }
    public void SetField(string fPath, JSValue value, Topic prim = null) {
      if (!TrySetField(fPath, value, prim)) throw new ArgumentException(this._path + ".SetField - empty field path");
    }
    /// <summary>Читает поле "attr" манифеста; возвращает false, если пригодного значения нет.</summary>
    private bool TryGetAttr(out int attr) {
      JSValue a;
      if (!_manifest.IsObject() || !(a = _manifest["attr"]).IsNumber) {
        attr = 0;
        return false;
      }
      attr = (int)a;
      return true;
    }
    public bool CheckAttribute(Attribute mask, Attribute value = Attribute.None) {
      if (value == Attribute.None) {
        value = mask;
      }
      if (!TryGetAttr(out int attr)) return false;
      return (attr & (int)mask) == (int)value;
    }
    public void SetAttribute(Attribute value) {
      JSL.Number attr;
      if (!TryGetAttr(out int old)) {
        attr = new JSL.Number((int)value);
      } else {
        // DB и Config взаимоисключающие
        if ((value & Attribute.Saved) != Attribute.None) {
          old &= ~((int)Attribute.Saved);
        }
        attr = new JSL.Number((int)value | old);
      }
      var c = new CmdField(this, "attr", attr, null);
      _repo.DoCmd(c);
    }
    public void ClearAttribute(Attribute value) {
      JSL.Number attr;
      if (!TryGetAttr(out int old)) {
        attr = new JSL.Number((int)value);
      } else {
        attr = new JSL.Number(old & ~(int)value);
      }
      var c = new CmdField(this, "attr", attr, null);
      _repo.DoCmd(c);
    }

    public int CompareTo(Topic other) {
      if (other == null) {
        return 1;
      }
      return this._path.CompareTo(other._path);
    }
    public override string ToString() {
      return _path;
    }

    #region helpers
    public class Bill : IEnumerable<Topic> {
      public const char delmiter = '/';
      public const string delmiterStr = "/";
      public const string maskAll = "#";
      public const string maskChildren = "+";
      public static readonly char[] delmiterObj = new char[] { '.' };
      public static readonly char[] delmiterArr = new char[] { delmiter };
      public static readonly string[] curArr = new string[0];
      public static readonly string[] allArr = new string[] { maskAll };
      public static readonly string[] childrenArr = new string[] { maskChildren };

      private Topic _home;
      private bool _deep;

      public Bill(Topic home, bool deep) {
        _home = home;
        _deep = deep;
      }

      /// <summary>Обходит дочерние узлы либо всё поддерево в порядке имён Ordinal.</summary>
      /// <remarks>Ничего не сортируется и не копируется: дочерние узлы уже хранятся в неизменяемом массиве
      /// в нужном порядке. Обход один раз читает поле и перебирает полученный снимок. 
      /// <para>При глубоком обходе дочерние узлы помещаются в стек с конца, поэтому извлекаются в возрастающем
      /// порядке. Родитель всегда возвращается раньше своих потомков.</para></remarks>
      public IEnumerator<Topic> GetEnumerator() {
        if (!_deep) {
          KeyValuePair<string, Topic>[] kids = _home._children;
          for (int i = 0; i < kids.Length; i++) {
            if (!kids[i].Value.disposed) {  // Remove() немедленно помечает узел удалённым, а отсоединение происходит тиком позже
              yield return kids[i].Value;
            }
          }
          yield break;
        } else {
          var hist = new Stack<Topic>();
          Topic cur;
          hist.Push(_home);
          do {
            cur = hist.Pop();
            // _home возвращается даже при disposed: каскад удаления Repo обходит src.all, и только что
            // удалённый топик должен попасть в обход, чтобы получить команду отсоединения
            yield return cur;
            KeyValuePair<string, Topic>[] kids = cur._children;
            for (int i = kids.Length - 1; i >= 0; i--) {
              if (!kids[i].Value.disposed) {  // отдельно удалённый потомок обрабатывается каскадом собственной команды
                hist.Push(kids[i].Value);
              }
            }
          } while (hist.Count > 0);
        }
      }
      System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() {
        return GetEnumerator();
      }
    }

    /// <summary>Все события, публикуемые репозиторием. Dispose the result to stop receiving.</summary>
    public static IDisposable Subscribe(Action<TopicEvent> func) {
      if (_repo != null) {
        return _repo.SubscribeAll(func);
      } else {
        Log.Error("Topic.Subscribe({0}.{1}) - _repo == null", func.Target != null ? func.Target.ToString() : func.Method.DeclaringType.Name, func.Method.Name);
        throw new NullReferenceException("Topic.Subscribe() - _repo == null");
      }
    }
    /// <summary>Определяет, может ли строка быть собственным именем топика.</summary>
    public static bool IsValidName(string name) {
      return !string.IsNullOrWhiteSpace(name)
        && name.IndexOf(Bill.delmiter) < 0
        && name != Bill.maskAll
        && name != Bill.maskChildren;
    }
    /// <summary>Выбрасывает исключение, если строка не может быть собственным именем топика.</summary>
    internal static void CheckName(string name, string context) {
      if (!IsValidName(name)) {
        throw new ArgumentException(context + " - not a topic name: \"" + (name ?? "<null>") + "\"");
      }
    }
    /// <summary>Выбрасывает исключение, если хотя бы один сегмент пути не может быть именем топика.</summary>
    /// <remarks>Путь не является именем: он может быть равен "/" и содержать несколько сегментов, поэтому
    /// проверяется посегментно, а пустые сегменты пропускаются так же, как в Resolve.</remarks>
    internal static void CheckPath(string path, string context) {
      string[] segments = (path ?? string.Empty).Split(Bill.delmiterArr, StringSplitOptions.RemoveEmptyEntries);
      for (int i = 0; i < segments.Length; i++) {
        CheckName(segments[i], context);
      }
    }
    public static string JsValueTypeName(JSValue value) {
      if (value == null) return null;
      switch (value.ValueType) {
      case JSValueType.Object:
        if (value.Value == null) return "Null";
        // IsByteArray учитывает оба представления: сам JSValue и его свойство .Value
        if (X13.ByteArray.IsByteArray(value, out _)) return "ByteArray";
        return "Object";
      case JSValueType.String: {
          string text = value.AsString(null);
          if (text != null && text.StartsWith("¤VR")) return "Version";
          return "String";
        }
      case JSValueType.Boolean: return "Boolean";
      case JSValueType.Double:
      case JSValueType.Integer: return "Double";
      case JSValueType.Date: return "Time";
      default: return null;

      }
    }
    /// <summary>Возвращает эквивалентную подписку либо null. По этому признаку Subscribe устраняет дубликаты.</summary>
    /// <remarks>Сравнение setTopic не требуется: каждая запись в собственном массиве топика создана на нём.</remarks>
    private static SubRec Find(SubRec[] subs, Action<TopicEvent, SubRec> func, SubRec.SubMask mask, string prefix) {
      for (int i = 0; i < subs.Length; i++) {
        SubRec s = subs[i];
        if (s.func == func && s.mask == mask
            && ((mask & SubRec.SubMask.Field) == SubRec.SubMask.None || s.prefix == prefix)) {
          return s;
        }
      }
      return null;
    }

    /// <summary>Находит или создаёт топик, манифест которого будет задан позднее.</summary>
    /// <remarks>Declare и <see cref="Fill"/> образуют пару для топиков, метаданные которых поступают вместе
    /// с ними, например при восстановлении из хранилища или чтении из .xst. Если сообщить о создании до установки
    /// манифеста, подписчики увидят топик без атрибутов. Поэтому Declare не публикует событие, а Fill публикует.
    /// <para>Следовательно, объявленный, но не заполненный топик невидим: он находится в дереве и доступен по пути,
    /// но ни одно событие не сообщало о его появлении. Код, вызвавший Declare, отвечает и за вызов Fill.</para></remarks>
    public static Topic Declare(Topic home, string path, Topic prim = null) {
      return Resolve(home, path, true, prim, false);
    }

    /// <summary>Назначает объявленному топику манифест и состояние, затем публикует его создание.</summary>
    public static void Fill(Topic t, JSValue state, JSValue manifest, Topic prim) {
      t._manifest = (manifest == null || manifest.IsNull) ? JSObject.CreateObject() : manifest;
      if (!t._manifest["attr"].IsNumber) {
        t._manifest = JsLib.SetField(t._manifest, "attr", new JSL.Number(0));
      }

      var c = new CmdCreate(t, prim);
      _repo.DoCmd(c);

      if (state != null) {
        SetValue(t, state);
      }
    }

    private static Topic Resolve(Topic home, string path, bool create, Topic prim, bool fill) {
      if (path == Bill.delmiterStr) {
        return root;
      }
      if (string.IsNullOrEmpty(path)) {
        return home;
      }
      Topic next;
      if (path[0] == Bill.delmiter) {
        // Префикс должен заканчиваться на границе реального сегмента, иначе путь "/dev/light10/state" будет разрешён относительно home "/dev/light1" как дочерний путь "0/state"
        if (path.StartsWith(home._path) && (home._path.Length == 1 || path.Length == home._path.Length || path[home._path.Length] == Bill.delmiter)) {
          path = path.Substring(home._path.Length);
        } else {
          home = Topic.root;
        }
      }
      var pt = path.Split(Bill.delmiterArr, StringSplitOptions.RemoveEmptyEntries);
      for (int i = 0; i < pt.Length; i++) {
        CheckName(pt[i], home._path + "[" + path + "]");
        next = null;
        KeyValuePair<string, Topic>[] kids = home._children;
        int at = IndexOf(kids, pt[i]);
        if (at >= 0 && !kids[at].Value.disposed) {
          next = kids[at].Value;
        }
        if (next == null) {
          if (!create) {
            return null;
          }
          // Выполняется под структурной блокировкой, чтобы Move мог рассчитывать на свободное имя.
          // Добавление между проверкой и вставкой в Move оставило бы перемещаемый топик вне дерева.
          lock (_structural) {
            kids = home._children;
            at = IndexOf(kids, pt[i]);
            if (at >= 0 && !kids[at].Value.disposed) {
              next = kids[at].Value;   // другой поток пришёл первым; используем опубликованный им узел
            } else {
              // Запись с disposed заменяется, а не возвращается. Remove() помечает топик и откладывает
              // отсоединение до тика, а запросившему путь в этот промежуток нужен пригодный к использованию топик,
              // а не удаляемый. Unlink удаляет пару, поэтому ожидающее удаление старого топика не затронет замену.
              next = new Topic(home, pt[i], fill);
              home._children = at >= 0 ? Replaced(kids, at, pt[i], next) : Inserted(kids, ~at, pt[i], next);
              if (fill) {  // иначе команда создания добавляется в Fill()
                _repo.DoCmd(new CmdCreate(next, prim));
              }
            }
          }
        }
        home = next;
      }
      return home;
    }
    internal static void SetValue(Topic t, JSValue val) {
      t._state = val;
    }
    /// <summary>Добавляет одну запись манифеста в результат, формируемый текущим тиком для топика.</summary>
    /// <returns>Пакет, общий для всех записей в этот топик в текущем тике. Вызывающий код сохраняет его:
    /// первая применённая запись устанавливает сформированный манифест, а все записи получают
    /// из пакета манифест, существовавший до начала обработки.</returns>
    /// <remarks>Объединение сохраняет согласованность манифеста: подписчики видят один новый манифест,
    /// а не частичный результат каждой записи.</remarks>
    internal static FieldBatch SetField(CmdField cmd) {
      Topic t = cmd.Target;
      if (t._mfst_pu == null) {
        t._mfst_pu = new FieldBatch { value = t._manifest ?? JSValue.Null };
      }
      t._mfst_pu.value = JsLib.SetField(t._mfst_pu.value, cmd.Path, cmd.Value);
      return t._mfst_pu;
    }

    /// <summary>Устанавливает манифест, сформированный текущим тиком. Выполняется один раз для топика при применении первой записи.</summary>
    /// <remarks>Безопасно очищать _mfst_pu здесь, а не в конце тика: поле читается только при извлечении
    /// команд из очереди, которое завершается до применения команд, а каждая команда пакета уже хранит ссылку
    /// на сам пакет.</remarks>
    internal static void SetField2(Topic t, FieldBatch batch) {
      if (batch.swapped) {
        return;
      }
      batch.swapped = true;
      batch.oldManifest = System.Threading.Interlocked.Exchange(ref t._manifest, batch.value);
      t._mfst_pu = null;
    }

    /// <summary>Позиция имени среди дочерних узлов: индекс либо ~(точка вставки).</summary>
    /// <remarks>Используется соглашение Array.BinarySearch, но без выделения памяти, которое потребовалось бы
    /// для создания искомого KeyValuePair. Сравнение Ordinal соответствует порядку хранения массива.
    /// <para>Метод не имеет побочных эффектов, поэтому читатель может вызывать его для своего снимка без блокировки,
    /// а писатель вызывает его для массива, прочитанного внутри блокировки.</para></remarks>
    private static int IndexOf(KeyValuePair<string, Topic>[] kids, string name) {
      int lo = 0, hi = kids.Length - 1;
      while (lo <= hi) {
        int mid = lo + ((hi - lo) >> 1);
        int c = string.CompareOrdinal(kids[mid].Key, name);
        if (c == 0) {
          return mid;
        }
        if (c < 0) {
          lo = mid + 1;
        } else {
          hi = mid - 1;
        }
      }
      return ~lo;
    }

    /// <summary>Три способа изменения дочерних узлов. Каждый создаёт и возвращает новый массив.</summary>
    /// <remarks>Все три метода не имеют побочных эффектов и не изменяют поля. Структурная блокировка нужна
    /// вызывающему коду не для них, а для окружающей последовательности «прочитать, принять решение, опубликовать».</remarks>
    private static KeyValuePair<string, Topic>[] Inserted(KeyValuePair<string, Topic>[] kids, int at, string name, Topic child) {
      var next = new KeyValuePair<string, Topic>[kids.Length + 1];
      Array.Copy(kids, 0, next, 0, at);
      next[at] = new KeyValuePair<string, Topic>(name, child);
      Array.Copy(kids, at, next, at + 1, kids.Length - at);
      return next;
    }
    private static KeyValuePair<string, Topic>[] Replaced(KeyValuePair<string, Topic>[] kids, int at, string name, Topic child) {
      var next = (KeyValuePair<string, Topic>[])kids.Clone();
      next[at] = new KeyValuePair<string, Topic>(name, child);
      return next;
    }
    private static KeyValuePair<string, Topic>[] RemovedAt(KeyValuePair<string, Topic>[] kids, int at) {
      if (kids.Length == 1) {
        return NoChildren;
      }
      var next = new KeyValuePair<string, Topic>[kids.Length - 1];
      Array.Copy(kids, 0, next, 0, at);
      Array.Copy(kids, at + 1, next, at, kids.Length - at - 1);
      return next;
    }

    private static void UpdatePath(Topic t) {
      t._path = t.parent == root ? "/" + t._name : t.parent._path + "/" + t._name;
      KeyValuePair<string, Topic>[] kids = t._children;
      for (int i = 0; i < kids.Length; i++) {
        UpdatePath(kids[i].Value);
      }
    }

    /// <summary>Удаляет топик из дерева под той же блокировкой, что используется для создания и перемещения.</summary>
    /// <remarks>Удаляется пара, а не имя: между установкой отметки в Remove() и выполнением этого метода
    /// тиком позже кто-либо мог запросить тот же путь и получить новый топик. Отсоединение только по имени
    /// ошибочно удалило бы новый топик.</remarks>
    internal static void Unlink(Topic t) {
      t.disposed = true;
      Topic parent = t._parent;
      if (parent != null) {
        lock (_structural) {
          KeyValuePair<string, Topic>[] kids = parent._children;
          int at = IndexOf(kids, t._name);
          if (at >= 0 && kids[at].Value == t) {
            parent._children = RemovedAt(kids, at);
          }
        }
      }
    }
    /// <summary>Передаёт изменение каждой подписке, распространяющейся на этот топик.</summary>
    /// <remarks>Регистрации хранятся в топике, на котором были созданы, поэтому для их поиска выполняется
    /// обход вверх: собственные записи топика обрабатывают Once и All, записи родителя — Children и All,
    /// а записи каждого вышестоящего предка — только All. Разделение уровней не позволяет дважды вызвать запись,
    /// маска которой одновременно содержит Children и All.
    /// <para>У перемещения таких цепочек две. Структура меняется сразу, на потоке вызвавшего, а публикация
    /// идёт тиком позже, поэтому обычный обход поднимается по новому месту и о переезде узнаёт только тот,
    /// кто следит за местом назначения. Следящий за составом СВОИХ детей обязан узнать и об уходе.</para></remarks>
    internal static void Publish(TopicEvent e) {
      if ((e.Kind == EventKind.Snapshot || e.Kind == EventKind.Ready) && e.Sub != null) {
        Invoke(e.Sub, e);   // адресовано одной подписке, а не всем наблюдателям топика
        return;
      }
      Topic t = e.Source;
      Deliver(t, e, SubRec.SubMask.OnceOrAll);
      Topic a = t.parent;
      if (a != null) {
        Deliver(a, e, SubRec.SubMask.Children | SubRec.SubMask.All);
        for (a = a.parent; a != null; a = a.parent) {
          Deliver(a, e, SubRec.SubMask.All);
        }
      }
      if (e.Kind == EventKind.Moved && e.OldParent != null) {
        // Обе цепочки сходятся на общем предке и выше идут одной. Обход прекращается на первом же
        // таком узле: всё, что там есть, уже вызвано обходом по новому месту, и продолжение означало бы
        // второй вызов одной подписки. Переименование - предельный случай: родитель не менялся, он и есть
        // общий предок, и второй обход не делает ни шага.
        SubRec.SubMask scope = SubRec.SubMask.Children | SubRec.SubMask.All;
        for (Topic o = e.OldParent; o != null && !IsAncestorOf(o, t); o = o.parent) {
          Deliver(o, e, scope);
          scope = SubRec.SubMask.All;   // прежний родитель - один уровень, всё выше него - только All
        }
      }
    }

    /// <summary>Является ли узел предком топика. Сам себе топик предком не считается.</summary>
    private static bool IsAncestorOf(Topic node, Topic t) {
      for (Topic a = t._parent; a != null; a = a._parent) {
        if (object.ReferenceEquals(a, node)) {
          return true;
        }
      }
      return false;
    }

    /// <param name="scope">Маски, распространяющиеся с этого уровня на e.Source.</param>
    private static void Deliver(Topic node, TopicEvent e, SubRec.SubMask scope) {
      // Поле читается один раз, затем обходится полученный снимок. Обработчик может освободить собственную
      // или чужую регистрацию, поэтому обход не должен индексировать изменённый массив.
      SubRec[] subs = node._subRecords;
      for (int i = 0; i < subs.Length; i++) {
        SubRec sb = subs[i];
        if ((sb.mask & scope) == SubRec.SubMask.None) {
          continue;
        }
        if (e.Kind == EventKind.StateChanged && (sb.mask & SubRec.SubMask.Value) != SubRec.SubMask.Value) {
          continue;
        }
        if (e.Kind == EventKind.FieldChanged
            && ((sb.mask & SubRec.SubMask.Field) != SubRec.SubMask.Field
                || !SameBranch(e.FieldPath, sb.prefix)
                || JsLib.SameValue(e.OldManifest.Field(sb.prefix ?? string.Empty), e.Source._manifest.Field(sb.prefix ?? string.Empty)))) {
          continue;
        }
        Invoke(sb, e);
      }
    }

    /// <summary>Возвращает true, если путь записанного поля и префикс подписки находятся в одной ветке.</summary>
    /// <remarks>Проверка необходима после того, как запись манифеста стала сообщать собственный путь.
    /// Без неё подписчик одного поля вызывался бы для каждого поля, записанного в топик за тик, поскольку
    /// сравнение манифестов ниже даёт одинаковый результат для каждого события пакета.
    /// <para>Учитываются оба направления, каждое по своей причине. Путь внутри префикса является обычным случаем:
    /// префикс "MQTT-SN", запись "MQTT-SN.gr". Префикс внутри пути легко упустить: если префикс равен "MQTT.uri",
    /// то запись, заменяющая весь объект "MQTT", также изменяет поле подписчика.</para>
    /// <para>Сравнение выполняется посегментно, а не как строковый префикс, иначе "MQTT-SNx" считался бы записью
    /// внутри "MQTT-SN". Разбиение выполняется так же, как в GetField, чтобы оба метода одинаково определяли
    /// сегменты, включая завершающую точку, которую RemoveEmptyEntries отбрасывает.</para></remarks>
    private static bool SameBranch(string path, string prefix) {
      if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(path)) {
        return true;   // отсутствие префикса означает любое поле
      }
      string[] a = path.Split(Bill.delmiterObj, StringSplitOptions.RemoveEmptyEntries);
      string[] b = prefix.Split(Bill.delmiterObj, StringSplitOptions.RemoveEmptyEntries);
      int n = a.Length < b.Length ? a.Length : b.Length;
      for (int i = 0; i < n; i++) {
        if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) {
          return false;
        }
      }
      return true;
    }

    private static void Invoke(SubRec sb, TopicEvent e) {
      try {
        sb.func(e, sb);
      }
      catch (Exception ex) {
        PluginFailed(sb.func.Method.DeclaringType.Name + "." + sb.func.Method.Name, e, ex);
      }
    }

    /// <summary>Сообщает об ошибке стороннего обработчика через встроенное ограничение частоты сообщений тика.</summary>
    /// <remarks>Здесь используется не простой Log.Warning, поскольку неисправный подписчик выбрасывает исключение
    /// для каждого события, а тик выполняется примерно шестьдесят раз в секунду. Без ограничения один сломанный
    /// плагин заполнит журнал и скроет сообщение, указывающее на него. Ограничением управляет Repo; этот метод
    /// служит точкой входа для статических путей доставки, у которых нет собственного экземпляра репозитория.</remarks>
    internal static void PluginFailed(string who, object subject, Exception ex) {
      Repo repo = _repo;
      if (repo != null) {
        repo.PluginFailed(who, subject, ex);
      } else {
        Log.Warning("{0}({1}) - {2}", who, subject, ex);
      }
    }

    internal static bool Unsubscribe(Topic t, SubRec sr) {
      return RemoveSubscripton(t, sr);
    }

    /// <summary>Удаляет одну подписку из топика, на котором она была создана.</summary>
    /// <remarks>Используется копирование при записи, как в Subscribe: уже начавшая обход старого массива доставка
    /// завершает его по регистрациям, которые были активны в момент начала.</remarks>
    private static bool RemoveSubscripton(Topic t, SubRec sr) {
      lock (t._sync) {
        SubRec[] old = t._subRecords;
        int idx = Array.IndexOf(old, sr);
        if (idx < 0) {
          return false;
        }
        SubRec[] next = new SubRec[old.Length - 1];
        Array.Copy(old, 0, next, 0, idx);
        Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);
        t._subRecords = next;
        return true;
      }
    }
    [Flags]
    public enum Attribute {
      None = 0,
      Required = 1,
      Readonly = 2,
      DB = 4,
      Config = 8,
      Saved = Attribute.DB | Attribute.Config,
      Internal = 64,

    }
    #endregion helpers
  }
}

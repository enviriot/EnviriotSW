///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using System;
using System.Threading;
using X13.Repository;

namespace X13 {
  /// <summary>Плоское пространство имён обработчиков, каждый из которых вызывается для одного топика.</summary>
  public static class RPC {
    // Потокобезопасная коллекция: плагины регистрируют обработчики из Init()/Start() в основном потоке,
    // тогда как рабочие потоки, например PersistentStorage, уже могут вызывать Call. Обычный Dictionary
    // для этого небезопасен.
    //
    // Для обеих форм регистрации используется один словарь, а не два: пространство имён должно оставаться
    // единым. Иначе проверка повторяющегося имени ниже позволила бы зарегистрировать одно имя по разу в каждой
    // коллекции, а Call молча выбрал бы один обработчик. Обработчик без ответа преобразуется при регистрации,
    // поэтому все записи имеют одинаковую сигнатуру и для каждой действует правило «ровно один ответ на вызов».
    // См. Register(name, Action<Topic, JSValue>).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, JSC.JSValue, Action<JSC.JSValue>>> _list
      = new System.Collections.Concurrent.ConcurrentDictionary<string, Action<Topic, JSC.JSValue, Action<JSC.JSValue>>>();

    /// <summary>Регистрирует обработчик, которому нечего возвращать вызывающему коду.</summary>
    /// <remarks>Возвращает <c>undefined</c> сразу после завершения обработчика, чтобы ожидающий ответ вызывающий код
    /// не оставался в ожидании обработчика, который не собирался ничего отправлять. Вызывающий код таких
    /// обработчиков уже рассчитывает на это поведение: действия меню в base.xst всегда выполнялись без ожидания
    /// результата. Кроме того, это сохраняет единый инвариант ответа для каждого зарегистрированного имени.</remarks>
    public static void Register(string name, Action<Topic, JSC.JSValue> cb) {
      if(cb == null) {
        throw new ArgumentNullException("cb");
      }
      Register(name, new Action<Topic, JSC.JSValue, Action<JSC.JSValue>>((t, arg, reply) => {
        cb(t, arg);
        reply(JSC.JSValue.Undefined);
      }));
    }

    /// <summary>Регистрирует обработчик, возвращающий ответ, возможно позднее и из другого потока.</summary>
    /// <remarks><paramref name="cb"/> получает делегат ответа и должен вызвать его ровно один раз после завершения
    /// работы. Повторный вызов безвреден: Call оборачивает делегат так, что передаётся только первый ответ.
    /// Однако отсутствие вызова оставляет вызывающий код в ожидании, поэтому обработчик, способный завершиться
    /// ошибкой, должен вернуть информацию об ошибке, а не завершаться молча.</remarks>
    public static void Register(string name, Action<Topic, JSC.JSValue, Action<JSC.JSValue>> cb) {
      if(!_list.TryAdd(name, cb)) {  // сохраняет контракт Dictionary.Add: повторяющееся имя является ошибкой
        throw new ArgumentException("RPC.Register - duplicate name: " + name);
      }
    }

    /// <summary>Вызывает зарегистрированный обработчик для одного топика.</summary>
    /// <param name="t">Топик, к которому относится вызов и на котором объявлено действие.</param>
    /// <param name="arg">Значение, переданное вызывающим кодом, либо undefined.</param>
    /// <param name="reply">Вызывается с ответом обработчика не более одного раза. Может отсутствовать, если вызывающему коду
    /// ответ не требуется.</param>
    /// <returns>Возвращает false, если обработчик с таким именем не зарегистрирован. Это позволяет вызывающему коду
    /// определить, что вызов не был обработан, вместо ожидания ответа, который не придёт.</returns>
    /// <remarks>Защита от повторного ответа реализована здесь, а не в каждом обработчике.</remarks>
    public static bool Call(string name, Topic t, JSC.JSValue arg, Action<JSC.JSValue> reply = null) {
      Action<Topic, JSC.JSValue, Action<JSC.JSValue>> cb;
      if(!_list.TryGetValue(name, out cb)) {
        return false;
      }
      int answered = 0;
      cb.Invoke(t, arg ?? JSC.JSValue.Undefined, v => {
        if(Interlocked.Exchange(ref answered, 1) == 0 && reply != null) {
          reply(v);
        }
      });
      return true;
    }
  }
}

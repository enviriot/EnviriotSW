///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Archivist {
  /// <summary>Архивный отсчёт с индексом топика в списке запроса.</summary>
  /// <remarks>T задано в UTC.
  /// V хранится как double: архив содержит числа, поэтому создавать JSValue для каждого прочитанного отсчёта до формирования ответа не требуется.</remarks>
  internal readonly struct ArchSample {
    public readonly int Idx;
    public readonly DateTime T;
    public readonly double V;

    public ArchSample(int idx, DateTime t, double v) {
      Idx = idx;
      T = t;
      V = v;
    }
  }

  /// <summary>Преобразует упорядоченный по времени поток отсчётов в строки ответа AQuery.</summary>
  /// <remarks>Участвует в чтении истории. Не обращается к хранилищу: выбор исходных отсчётов или свёрток остаётся за вызывающей стороной.</remarks>
  internal static class ArchAccumulator {

    /// <summary>Формирует строки исходных значений без усреднения.</summary>
    /// <remarks>Отсчёт может заполнить свободный столбец текущей строки. При прямом ходе окно менее 15 секунд от начала строки объединяет близкие отсчёты разных топиков,
    /// а не только значения с одинаковым временем.</remarks>
    internal static JSL.Array Raw(IEnumerable<ArchSample> src, int topicCount) {
      var rez = new JSL.Array();
      JSL.Array lo = null;
      DateTime rowT = DateTime.MinValue;          // Время начала текущей строки в UTC.
      foreach(var s in src) {
        int slot = s.Idx + 1;                     // Столбец 0 занят временной меткой.
        // Проверка ValueType учитывает JSValue.Null: его тип Object обозначает свободный столбец. IsObject() исключает Null и не позволил бы дополнить строку.
        // Интервал считаем по rowT в UTC, а не по локальной метке из столбца 0.
        if(lo != null && lo[slot].ValueType == JSC.JSValueType.Object && (s.T - rowT).TotalSeconds < 15) {
          lo[slot] = new JSL.Number(s.V);
        } else {
          rowT = s.T;
          lo = new JSL.Array(topicCount + 1) {
            [0] = X13.JsExtLib.Context.ProxyValue(s.T.ToLocalTime())
          };
          for(var j = 1; j <= topicCount; j++) {
            lo[j] = (slot == j) ? (JSC.JSValue)new JSL.Number(s.V) : JSC.JSValue.Null;
          }
          rez.Add(lo);
        }
      }
      return rez;
    }

    /// <summary>Пересчитывает возрастающий по времени поток в корзины с шагом (end - begin) / |count|.</summary>
    /// <remarks>Границы и отсчёты заданы в UTC. Промежуточные пустые корзины могут пропускаться, поэтому count задаёт шаг, а не точное число строк ответа.</remarks>
    /// <param name="seed">Известное на начало окна значение каждого ряда или NaN, если его нет. 
    /// Заполняет начало первой корзины. Поиск выполняет вызывающая сторона: это задача чтения хранилища, а не расчёта.</param>
    internal static JSL.Array Resample(IEnumerable<ArchSample> src, DateTime begin, DateTime end, int count, double[] seed) {
      int topicCount = seed.Length;
      var rez = new JSL.Array();
      var step = (end - begin).TotalSeconds / Math.Abs(count);

      DateTime cursor = begin.AddSeconds(step);
      var f_cnt = new int[topicCount];
      var f_val = new double[topicCount];
      var l_val = new double[topicCount];
      var l_delta = new double[topicCount];
      var t_cnt = 0;
      double t_sum = 0;
      int i;

      for(i = 0; i < topicCount; i++) {
        f_val[i] = 0;
        f_cnt[i] = 0;
        l_delta[i] = -step;
        l_val[i] = seed[i];
      }

      foreach(var s in src) {
        var t_cur = s.T;
        if(t_cur >= cursor) {
          AddRecord();
          do {
            cursor = cursor.AddSeconds(step);
          } while(t_cur >= cursor);
        }
        i = s.Idx;
        if(i < 0 || i >= topicCount) {
          continue;
        }
        var v = s.V;
        // Исключаем и NaN, и бесконечность: один такой отсчёт испортил бы взвешенную сумму и переносимое значение ряда.
        if(!double.IsNaN(v) && !double.IsInfinity(v)) {
          var td = (t_cur - cursor).TotalSeconds;
          if(!double.IsNaN(l_val[i])) {
            f_val[i] += l_val[i] * (td - l_delta[i]) / step;
            l_delta[i] = td;
          }
          f_cnt[i]++;
          l_val[i] = v;
          t_cnt++;
          t_sum += td;
        }
      }
      AddRecord();
      return rez;

      void AddRecord() {
        JSL.Array lo = new JSL.Array(topicCount + 1) {
          [0] = X13.JsExtLib.Context.ProxyValue(cursor.AddSeconds(t_cnt == 1 ? t_sum : (-step / 2)).ToLocalTime())
        };
        t_cnt = 0;
        t_sum = 0;
        for(i = 0; i < topicCount; i++) {
          // При нескольких отсчётах ряда выдаём взвешенное по времени среднее, при менее чем двух — последнее известное значение, а при его отсутствии — Null.
          // Это правило чтения, не расчёт сохраняемой свёртки. l_val не сбрасываем: перенос значения позволяет не разрывать медленно меняющийся ряд.
          lo[i + 1] = f_cnt[i] > 1
            ? (JSC.JSValue)new JSL.Number(f_val[i] + l_val[i] * (-l_delta[i]) / step)
            : (double.IsNaN(l_val[i]) ? JSC.JSValue.Null : new JSL.Number(l_val[i]));
          f_val[i] = 0;
          f_cnt[i] = 0;
          l_delta[i] = -step;
        }
        rez.Add(lo);
      }
    }
  }
}

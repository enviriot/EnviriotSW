///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;

namespace X13.Archivist {

  /// <summary>Результат свёртки одного интервала для сохранения в архиве.</summary>
  internal struct Bucket {
    /// <summary>Число учтённых отсчётов; при нуле корзина не сохраняется.</summary>
    internal int N;
    /// <summary>Взвешенное по времени среднее за весь интервал.</summary>
    internal double V;
    /// <summary>Последнее значение входного потока для продолжения расчёта в следующем интервале.</summary>
    internal double Last;
  }

  /// <summary>Строит свёртки истории и продвигает курсоры обработанных часов.</summary>
  internal static class ArchRollup {
    /// <summary>Запас времени между текущим моментом и границей свёртки завершённых часов.</summary>
    internal static readonly TimeSpan ROLL_LAG = TimeSpan.FromDays(ArchStore.HOT_KEEP_DAYS);

    /// <summary>Сворачивает очередной час ряда и при завершении суток формирует дневную корзину.</summary>
    /// <returns>True, если обработанный час содержит пригодные отсчёты; пустой час тоже продвигает курсор.</returns>
    /// <remarks>Пройденные часы автоматически не пересчитываются ни при поздней записи, ни при последующем удалении исходных отсчётов.</remarks>
    internal static bool FoldHour(ArchStore st, ArchTopic at) {
      if(at == null || at.Hot) return false;                              // Короткие ряды hot не сворачиваются.
      DateTime nowUtc = DateTime.UtcNow;
      if(at.Rt == DateTime.MinValue) {
        at.Rt = ArchTime.HourFloor(nowUtc);        // При отсутствии курсора начинаем с текущего часа.
        st.Store(at);
        return false;
      }
      long idx = ArchTime.HourIndex(at.Rt);
      if(idx >= ArchTime.HourIndex(nowUtc - ROLL_LAG)) return false;
      DateTime bs = ArchTime.HourStart(idx), be = bs.AddHours(1);
      var ids = new[] { at.Id };
      double seed = st.Seed(ids, bs, ArchTime.GRAN_RAW)[0];
      // Верхняя граница чтения включена: отступаем на тик от следующего часа, чтобы граничный отсчёт не попал в обе корзины.
      var buf = st.Stream(ids, bs, be.AddTicks(-1), false, 0).ToList();
      var b = FoldInto(st, at, buf, idx, seed);
      at.Rt = be;
      st.Store(at);

      if(ArchTime.DayIndex(be) > ArchTime.DayIndex(bs)) {
        FoldDay(st, at, ArchTime.DayIndex(bs));
      }
      return b.N > 0;
    }

    /// <summary>Строит часовую и двенадцать пятиминутных корзин из общего буфера исходного часа.</summary>
    /// <remarks>Пятиминутный уровень уменьшает объём чтения графиков без повторного чтения исходного часа при свёртке. Пустые корзины хранилище не записывает.</remarks>
    internal static Bucket FoldInto(ArchStore st, ArchTopic at, List<ArchSample> buf, long hourIdx, double seed) {
      DateTime bs = ArchTime.HourStart(hourIdx), be = bs.AddHours(1);
      var b = Fold(buf, bs, be, seed);
      st.UpsertBucket(ArchTime.GRAN_HOUR, at.Id, hourIdx, b);

      int pos = 0;
      double s5 = seed;
      int per = ArchTime.GRAN_HOUR / ArchTime.GRAN_5MIN;
      long sub0 = ArchTime.BucketIndex(bs, ArchTime.GRAN_5MIN);
      for(int k = 0; k < per; k++) {
        DateTime ss = bs.AddSeconds((double)k * ArchTime.GRAN_5MIN), se = ss.AddSeconds(ArchTime.GRAN_5MIN);
        // Буфер упорядочен по времени: выделяем очередной участок без повторного просмотра всего часа.
        int from = pos;
        while(pos < buf.Count && buf[pos].T < se) {
          pos++;
        }
        var b5 = Fold(buf.GetRange(from, pos - from), ss, se, s5);
        st.UpsertBucket(ArchTime.GRAN_5MIN, at.Id, sub0 + k, b5);
        if(b5.N > 0) {
          s5 = b5.Last;
        }
      }
      return b;
    }

    /// <summary>Строит свёртку завершённых суток UTC из часовых корзин, не из исходных отсчётов.</summary>
    /// <remarks>Часовые средние подаются с метками начала часа, а не середины: перенос из середины исказил бы вес первой и последней частей суток.</remarks>
    internal static void FoldDay(ArchStore st, ArchTopic at, long day) {
      DateTime ds = ArchTime.DayStart(day), de = ds.AddDays(1);
      long h0 = ArchTime.HourIndex(ds), h1 = ArchTime.HourIndex(de) - 1;

      var src = new List<ArchSample>();
      int n = 0;
      foreach(var kv in st.Buckets(ArchTime.GRAN_HOUR, at.Id, h0, h1)) {
        src.Add(new ArchSample(0, ArchTime.HourStart(kv.Key), kv.Value.V));
        n += kv.Value.N;
      }
      if(src.Count == 0) {
        return;
      }
      Bucket prev;
      double seed = st.TryLastBucket(ArchTime.GRAN_HOUR, at.Id, h0, out prev) ? prev.V : double.NaN;
      var d = Fold(src, ds, de, seed);
      // Сохраняем число исходных отсчётов, а не число участвовавших часовых корзин.
      d.N = n;
      st.UpsertBucket(ArchTime.GRAN_DAY, at.Id, day, d);
    }

    /// <summary>Вычисляет взвешенное по времени среднее, число отсчётов и последнее значение интервала.</summary>
    /// <param name="seed">Значение на начало интервала или NaN, если оно неизвестно. При NaN первый пригодный отсчёт заполняет начало интервала.</param>
    /// <remarks>Ожидает упорядоченные по времени отсчёты заданного интервала.
    /// Даже единственный отсчёт взвешивается с учётом начального значения — в отличие от правила чтения ArchAccumulator.Resample. Минимум и максимум не вычисляются.</remarks>
    internal static Bucket Fold(IEnumerable<ArchSample> src, DateTime begin, DateTime end, double seed) {
      var b = new Bucket { N = 0, V = double.NaN, Last = seed };
      double interval = (end - begin).TotalSeconds;
      if(interval <= 0) return b;
      double l_val = seed, f_val = 0, l_delta = 0;
      int n = 0;

      foreach(var s in src) {
        double v = s.V;
        if(double.IsNaN(v) || double.IsInfinity(v)) continue;
        double td = (s.T - begin).TotalSeconds;
        if(td < 0) {
          td = 0;
        } else if(td > interval) {
          td = interval;
        }
        if(!double.IsNaN(l_val)) {
          f_val += l_val * (td - l_delta) / interval;
          l_delta = td;
        }
        n++;
        l_val = v;
      }
      if(n == 0) return b;                                  // Нет пригодных отсчётов — корзина остаётся пустой.
      b.N = n;
      b.V = f_val + l_val * (interval - l_delta) / interval;
      b.Last = l_val;
      return b;
    }
  }
}

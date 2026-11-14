///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Archivist {

  /// <summary>Выбранный источник и объём чтения запроса для диагностического журнала.</summary>
  internal struct ArchQueryStat {
    /// <summary>Выбранная ширина исходной корзины в секундах; GRAN_RAW для исходных отсчётов, -1, если уровень не выбирался. 
    /// При чтении свёрток ответ может включать сырой хвост.</summary>
    internal int Gran;
    /// <summary>Число отсчётов и корзин, переданных накопителю; поиск начального значения не учитывается.</summary>
    internal long Rows;

    internal string Level {
      get {
        return Gran < 0 ? "-"
          : Gran == ArchTime.GRAN_RAW ? "raw"
          : Gran == ArchTime.GRAN_5MIN ? "5min"
          : Gran == ArchTime.GRAN_HOUR ? "hour" : "day";
      }
    }
  }

  /// <summary>Выбирает источники истории и формирует массив строк ответа AQuery.</summary>
  /// <remarks>Получает хранилище от вызывающей стороны и не управляет его жизненным циклом. Отбор данных отделён от записи и фонового обслуживания плагина.</remarks>
  internal static class ArchQuery {
    /// <summary>Предел модуля входного count, не гарантия числа строк после выравнивания окна.</summary>
    internal const int MAX_POINTS = 10000;
    /// <summary>Предел исходных отсчётов при чтении диапазона или сырого хвоста.</summary>
    /// <remarks>Страхует запросы, в том числе count == 0, от неограниченного чтения. Основной способ уменьшить объём чтения больших интервалов — выбор готовых свёрток.</remarks>
    internal const int MAX_RAW_ROWS = 500000;

    internal static JSL.Array Run(ArchStore st, string[] topics, DateTime begin, int count, DateTime end) {
      return Run(st, topics, begin, count, end, out _);
    }

    /// <summary>Выполняет запрос истории и возвращает статистику выбора источника и чтения.</summary>
    /// <remarks>Выбранный уровень и число прочитанных строк помогают объяснить затраты запроса, которые нельзя оценить по одной длительности или числу точек ответа.</remarks>
    internal static JSL.Array Run(ArchStore st, string[] topics, DateTime begin, int count, DateTime end, out ArchQueryStat stat) {
      stat = new ArchQueryStat { Gran = -1 };
      var rez = new JSL.Array();
      if(st == null || !st.IsOpen || topics == null || topics.Length == 0) return rez;                                 // Без открытого хранилища или путей результат пуст.
      if(count > MAX_POINTS) {
        count = MAX_POINTS;
      } else if(count < -MAX_POINTS) {
        count = -MAX_POINTS;
      }
      // Нормализуем границы в UTC; MinValue сохраняем как признак отсутствия end.
      begin = begin.ToUniversalTime();
      end = end == DateTime.MinValue ? end : end.ToUniversalTime();

      var ids = new int[topics.Length];
      for(int k = 0; k < topics.Length; k++) {
        var at = st.ByPath(topics[k]);
        ids[k] = at == null ? 0 : at.Id;
      }

      if(end <= begin || count == 0) {
        // Без пересчёта: диапазон при count == 0 либо отсчёты до/после begin, если конец окна не задан или не позже начала.
        DateTime lo, hi;
        bool desc;
        int limit;
        if(count < 0) {
          // До |count| отсчётов строго раньше begin, от новых к старым. Убираем граничный момент сдвигом на один тик ключа, то есть 100 нс.
          lo = DateTime.MinValue;
          hi = begin.AddTicks(-1);
          desc = true;
          limit = Math.Min(-count, MAX_RAW_ROWS);
        } else if(count == 0) {
          // Границы включены. При end < begin поток пуст, при равенстве возможен отсчёт на границе. Отсутствие корректного диапазона не заменяем чтением всей истории.
          lo = begin;
          hi = end;
          desc = false;
          limit = MAX_RAW_ROWS;
        } else {
          // До count отсчётов строго позже begin, от старых к новым. Сдвиг на тик исключает сам begin, как и в обратном направлении.
          lo = begin.AddTicks(1);
          hi = DateTime.MaxValue;
          desc = false;
          limit = Math.Min(count, MAX_RAW_ROWS);
        }
        stat.Gran = ArchTime.GRAN_RAW;
        var rows = new long[1];
        var res = ArchAccumulator.Raw(Counted(st.Stream(ids, lo, hi, desc, limit), rows), topics.Length);
        stat.Rows = rows[0];
        return res;
      }

      int gran = ArchTime.Granularity((end - begin).TotalSeconds / Math.Abs(count));
      stat.Gran = gran;
      Snap(gran, ref begin, ref end, ref count);
      DateTime horizon = st.RollupHorizon(ids);
      // Граница должна оставаться в UTC: сравнение DateTime не учитывает Kind.
      DateTime split = horizon == DateTime.MaxValue ? end : horizon;
      if(gran == ArchTime.GRAN_RAW || split <= begin) {
        // Нужна детализация выше доступных свёрток либо общая граница их готовности ещё не прошла начало окна.
        stat.Gran = ArchTime.GRAN_RAW;
        var rawRows = new long[1];
        var rawRes = ArchAccumulator.Resample(Counted(st.Stream(ids, begin, end, false, MAX_RAW_ROWS), rawRows), begin, end, count, st.Seed(ids, begin, ArchTime.GRAN_RAW));
        stat.Rows = rawRows[0];
        return rawRes;
      }
      // До общей границы читаем свёртки, после неё — исходные отсчёты.
      // Отдельные границы для каждого ряда потребовали бы дополнительного слияния потоков: простая конкатенация не обеспечила бы нужный накопителю порядок времени.
      IEnumerable<ArchSample> src = st.RollStream(gran, ids, begin, split < end ? split : end, false);
      if(split < end) {
        src = src.Concat(st.Stream(ids, split, end, false, MAX_RAW_ROWS));
      }
      var n = new long[1];
      var rez2 = ArchAccumulator.Resample(Counted(src, n), begin, end, count, st.Seed(ids, begin, gran));
      stat.Rows = n[0];
      return rez2;
    }

    /// <summary>Расширяет окно запроса наружу до границ абсолютной временной сетки.</summary>
    /// <remarks>При неизменном шаге сдвиг окна не меняет границы корзин; масштабирование может изменить шаг. 
    /// Сетка отсчитывается от начала календаря .NET.
    /// После расширения окна count пересчитывается и может превысить ограничение входного значения.</remarks>
    private static void Snap(int gran, ref DateTime begin, ref DateTime end, ref int count) {
      // Округляем шаг вверх до целых секунд для raw или целых исходных корзин для свёрток, чтобы не запрашивать детализацию внутри сохранённой корзины.
      long unit = gran == ArchTime.GRAN_RAW ? 1 : gran;
      double raw = (end - begin).TotalSeconds / Math.Abs(count);
      long stepSec = (long)Math.Ceiling(raw / unit) * unit;
      if(stepSec < unit) {
        stepSec = unit;
      }
      long step = stepSec * TimeSpan.TicksPerSecond;
      long b = begin.Ticks / step * step;
      long e = (end.Ticks + step - 1) / step * step;
      begin = new DateTime(b, DateTimeKind.Utc);
      end = new DateTime(e, DateTimeKind.Utc);
      count = (int)((e - b) / step);
      if(count < 1) {
        count = 1;
      }
    }

    /// <summary>Подсчитывает элементы, не изменяя поток.</summary>
    /// <remarks>Ячейка массива хранит общий счётчик: его читают после того, как накопитель завершил обход лениво вычисляемой последовательности.</remarks>
    private static IEnumerable<ArchSample> Counted(IEnumerable<ArchSample> src, long[] n) {
      foreach(var s in src) {
        n[0]++;
        yield return s;
      }
    }
  }
}

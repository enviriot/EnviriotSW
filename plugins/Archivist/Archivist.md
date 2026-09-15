# Archivist
Archivist накапливает историю значений и предоставляет общую модель чтения для графиков и JavaScript.

### Запись и чтение истории

```mermaid
flowchart TB
  ev["TopicEvent: StateChanged"](../../ --> sub["ArchivistPl.SubFunc"]
  sub --> acc["ArchAccumulator"]
  acc --> store["ArchStore: arch_raw, arch_hot"]
  store --> roll["ArchRollup: 5 минут, час, сутки"]
  roll --> meta["arch_meta"]
  chart["График в браузере"] --> api["/api/archivist"]
  js["JavaScript-блок Logram"] --> aq["JsExtLib.AQuery"]
  api --> aq
  aq --> query["ArchQuery: выбор уровня по шагу"]
  query --> store
  query --> meta
```

Запись начинается с подписки на `StateChanged`. Значения проходят через накопитель и сохраняются как сырые или оперативные данные. Фоновые свёртки строят уровни за пять минут, час и сутки.

Историю читают два основных клиента:

- HTTP-эндпоинт, используемый графиками WebUI;
- JavaScript через `JsExtLib.AQuery`.

Оба пути используют `ArchQuery`, который выбирает подходящий уровень хранения по запрошенному шагу. Клиент и сервер вычисляют одинаковые границы временных корзин, чтобы точки не смещались при панорамировании графика.

Очистка истории выполняется отдельно от записи и учитывает настроенный срок хранения. Запускается она по расписанию, чтобы не конкурировать с постоянным потоком новых значений.

Подробнее: [ArchivistPl](plugins/Archivist/ArchivistPl.cs)), [ArchStore](../../(plugins/Archivist/ArchStore.cs)), [ArchQuery](../../(plugins/Archivist/ArchQuery.cs)), [ArchTime](../../(plugins/Archivist/ArchTime.cs)), [ArchRetention](../../(plugins/Archivist/ArchRetention.cs)).

---

[Вернуться к основному README](../../README.md)

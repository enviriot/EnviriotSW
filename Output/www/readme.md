## lib/symbiote.js
Шаблонизатор [symbiotejs](https://symbiotejs.org/) используется для отображения компонент

## lib/stringformat.min.js
Библиотека для форматирования строк, дат и чисел [stringformat](https://github.com/dmester/sffjs)

Для поддержки других языков нужно скачать и поключить соответствующий [файл](https://github.com/dmester/sffjs/tree/master/dist/cultures)

## lib/wsBond.js
Модуль для общения Enviriot и компонент через Websockets

Если в компоненте есть атрибут с префиксом "data-", то значение из указанного топика при каждом изменении будет передаваться в соответствующий атрибут.

***

Конверторы:
### .format
Переводит значение в строки используя stringformat.min.js

`<x13-text data-value="/export/Test/T" data-value.format="0.0 °C"></x13-st_text>` ✧ 12.23 ➔ 12.2 °C

### .color
Переводит значение в hsla цвет

  `<x13-signal data-value="/export/Test/device" data-value.color="false:#FF7F7F;true:#7FFF7F" label="Device status"></x13-signal>`

Если топик для конвертора не указан, используется значение из data-value

  `<x13-text data-value="/export/Test/Pressure" data-value.format="0.00 mmHg" data-bg_color.color="685:#7FBFFF;710:#7FFF7F;735:#FFBF7F"></x13-text>`

***

## lib/dygraph.min.js
[JavaScript charting library](https://dygraphs.com/)

Используется в компонентах graph и wheather

# Компоненты
## components/button.js
Кнопка
Аттрибуты:
* bg_color - background color
* fg_color - foreground color
* value - Топик, куда публикуется значение. При клике - true, через 100 милисекунд - false

`<x13-button data-value="/export/Test/Mute" data-fg_color="/export/Test/Muted" data-fg_color.color="false:#40808080;true:#000000;">&#128277;</x13-button>`

## components/checkbox.js 
Кнопка с двумя стабильными состояниями
Аттрибуты:
* bg_color - background color
* value - нажато - true, отжато - false

`<x13-checkbox data-value="/export/Test/checkbox">&#127775;</x13-checkbox>`

## components/graph.js
График

Аттрибуты:
* period - Отобржаемый интервал в днях
* title - Заголовок
* ylabel - Подпись для оси y1
* y2label - Подпись для оси y2

data-* - Данные ось Y1
data-*.y2 - Данные ось Y2
Для накопления данных у указанного топика должен быть установлен в манифесте атррибут "arch" = true

## components/range.js
Ползунок

Аттрибуты:
* actual - текущее значение
* format - формат
* max - максимальное значение
* min - минимальное значение
* value - уставка

## components/signal.js
Кружок заданного цвета

Аттрибуты:
* label - подпись
* value - значение

## components/status.js
Статус устройства

Аттрибуты:
* value - false или 0 - offline, true или 1 - online, 2 - sleep

## components/stText.js
Текст с индикатором статуса

Аттрибуты:
* bg_color - background color
* fg_color - foreground color
* status - false или 0 - offline, true или 1 - online, 2 - sleep
* value - текст

## components/text.js
Текст

Аттрибуты:
* bg_color - background color
* fg_color - foreground color
* value - текст

## components/thermostat.js
Аттрибуты:
* humidity - текущая влажность
* setting - уставка
* status - true или 1 - online иначе offline
* temperature - текущая температура
* valve - мощность [-100..100]

## components/wheather.js
Прогноз погоды

Аттрибуты:
* forecast - прогноз погоды, преобразование из open-meteo

`let i = 0;
let now  = new Date();
let dt;
while(i<A.hourly.time.length && (dt = new Date(A.hourly.time[i]) < now)){
  i++;
}
if(i>0) i--;
let arr = []
for(let j=0;i<A.hourly.time.length && j<25; i++, j++){
  let r = {};
  r.dt =  new Date(A.hourly.time[i]);
  r.t = A.hourly.temperature_2m[i];
  r.w = A.hourly.windgusts_10m[i];
  r.u = A.hourly.uv_index[i];
  let wc = A.hourly.weathercode[i];
  r.i = (A.hourly.is_day[i]?'d':'n') + wc.toString();
  arr.push(r);
}
return arr;`
* temperature - текущая температура. Должен быть установлен аттрибут arch=true

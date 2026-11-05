export function localDay(year: number, month: number, day = 1): Date {
  const date = new Date('2000-01-01T00:00:00');
  date.setFullYear(year, month, day);
  return date;
}

export function dateKey(date: Date): string {
  return [
    String(date.getFullYear()).padStart(4, '0'),
    String(date.getMonth() + 1).padStart(2, '0'),
    String(date.getDate()).padStart(2, '0')
  ].join('-');
}

export function monthAt(date: Date, offset = 0): Date {
  return localDay(date.getFullYear(), date.getMonth() + offset);
}

export function calendarMonth(month: Date, locale: string, allowed: (date: string) => boolean): HTMLElement {
  const section = document.createElement('section');
  const title = document.createElement('h4');
  title.textContent = new Intl.DateTimeFormat(locale, { year: 'numeric', month: 'long' }).format(month);
  const grid = document.createElement('div');
  grid.className = 'grid';
  const weekdays = new Intl.DateTimeFormat(locale, { weekday: 'short' });
  for (let day = 0; day < 7; day++) {
    const label = document.createElement('span');
    label.className = 'weekday';
    label.textContent = weekdays.format(localDay(2023, 0, 1 + day));
    grid.append(label);
  }
  const days = localDay(month.getFullYear(), month.getMonth() + 1, 0).getDate();
  const label = new Intl.DateTimeFormat(locale, { year: 'numeric', month: 'short', day: 'numeric' });
  for (let index = 0; index < Math.ceil((month.getDay() + days) / 7) * 7; index++) {
    const day = index - month.getDay() + 1;
    if (day < 1 || day > days) {
      grid.append(document.createElement('span'));
      continue;
    }
    const date = localDay(month.getFullYear(), month.getMonth(), day);
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'day';
    button.dataset.date = dateKey(date);
    button.textContent = String(day);
    button.disabled = !allowed(button.dataset.date);
    button.setAttribute('aria-label', label.format(date));
    grid.append(button);
  }
  section.append(title, grid);
  return section;
}

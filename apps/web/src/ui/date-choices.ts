export const pad = (value: number): string => String(value).padStart(2, '0');
export const dateText = (value: Date): string =>
  `${value.getUTCFullYear()}-${pad(value.getUTCMonth() + 1)}-${pad(value.getUTCDate())}`;
export function calendarChoices(
  month: Date,
  value: string,
  chinese: boolean,
  allowed: (value: string) => boolean
): HTMLElement {
  const grid = document.createElement('div');
  grid.className = 'grid';
  grid.role = 'grid';
  for (const label of chinese
    ? ['日', '一', '二', '三', '四', '五', '六']
    : ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa']) {
    const day = document.createElement('span');
    day.className = 'weekday';
    day.textContent = label;
    grid.append(day);
  }
  const today = new Date();
  const todayText = `${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}`;
  for (let index = 0; index < 42; index++) {
    const date = new Date(month);
    date.setUTCDate(index - month.getUTCDay() + 1);
    const value = dateText(date),
      button = choiceButton(String(date.getUTCDate()), 'date', value);
    button.className =
      'day' +
      (date.getUTCMonth() !== month.getUTCMonth() ? ' outside' : '') +
      (value === todayText ? ' today' : '');
    button.role = 'gridcell';
    button.setAttribute('aria-label', value);
    button.setAttribute('aria-selected', String(value === value));
    button.disabled = !allowed(value);
    grid.append(button);
  }
  return grid;
}
export function timeChoices(hour: number, value: string, chinese: boolean): HTMLElement {
  const grid = document.createElement('div');
  grid.className = 'time-grid';
  for (const kind of ['hour', 'minute']) {
    const column = document.createElement('nexus-scroll-area');
    column.className = 'time-column';
    column.setAttribute(
      'label',
      kind === 'hour' ? (chinese ? '小时' : 'Hours') : chinese ? '分钟' : 'Minutes'
    );
    const items = document.createElement('div');
    items.className = 'time-items';
    column.append(items);
    for (let index = 0; index < (kind === 'hour' ? 24 : 60); index++) {
      const button = choiceButton(pad(index), kind, String(index));
      button.setAttribute(
        'aria-label',
        (kind === 'hour' ? (chinese ? '小时 ' : 'Hour ') : chinese ? '分钟 ' : 'Minute ') + pad(index)
      );
      button.setAttribute(
        'aria-pressed',
        String(kind === 'hour' ? index === hour : index === Number(value.split(':')[1]))
      );
      items.append(button);
    }
    grid.append(column);
  }
  return grid;
}
export function choiceButton(label: string, action: string, value = ''): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = label;
  button.dataset.action = action;
  button.dataset.value = value;
  return button;
}

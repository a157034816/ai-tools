export function suffix(value: string): string {
  if (value.length === 0) {
    return '-empty';
  }
  return `-${value}`;
}

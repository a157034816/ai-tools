/**
 * 包内第二个模块：被 index.ts 以 ES import 引入，
 * 用来证明脚本扩展可以跨包内文件共享类型与实现。
 */

/** 问候的措辞风格。 */
export enum GreetingStyle {
  /** 简洁：只报名字与次数。 */
  Plain = 'plain',

  /** 热情：附加一句欢迎语。 */
  Warm = 'warm'
}

/** 名字规范化选项。 */
export interface GreetingOptions {
  /** 原始名字；缺省时使用「世界」。 */
  name?: string;

  /** 措辞风格；缺省为 {@link GreetingStyle.Warm}。 */
  style?: GreetingStyle;
}

/** 名字格式化函数。 */
export type NameFormatter = (name: string) => string;

const MaxNameLength = 40;

/** 默认名字。 */
export const FallbackName = '世界';

/**
 * 整理用户输入的名字：去空白、截断、缺省回退。
 * @param value 原始输入。
 * @returns 可安全展示的名字。
 */
export function normalizeName(value: string | undefined): string {
  const text = (value ?? '').trim().slice(0, MaxNameLength);
  return text.length > 0 ? text : FallbackName;
}

/**
 * 生成交给模型的问候提示词。
 * @param options 名字与风格。
 * @param count 本次会话的问候序号。
 * @returns 提示词文本。
 */
export function buildGreetingPrompt(options: GreetingOptions, count: number): string {
  const name = normalizeName(options.name);
  const style = options.style ?? GreetingStyle.Warm;
  const suffix = style === GreetingStyle.Warm ? '语气热情一些。' : '一句话即可。';
  return `请用一句话向 ${name} 问好。这是本次会话第 ${count} 次问候，${suffix}`;
}

/**
 * 生成只显示、不触发模型轮次的说明文本。
 * @param count 当前计数。
 * @returns 展示文本。
 */
export function describeCount(count: number): string {
  return `本次会话已问候 ${count} 次。用法：\`/hello-ts <名字>\`。`;
}

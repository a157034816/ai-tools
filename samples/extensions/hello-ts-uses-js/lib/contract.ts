/**
 * 包内类型模块：定义跨包调用的结果契约，并实现调用本身。
 *
 * 这里刻意只使用「声明泛型」与具名导出类型，不使用 `import type`、也不使用调用点类型实参：
 * 扩展转译器只剥离声明位置的类型参数，且不支持 `import type` 语法。
 * 调用方只导入函数，结果类型通过返回值自然推导，无需再导入类型名。
 */

/** 一次跨包调用的结果。 */
export interface CallOutcome {
  /** 调用是否成功。 */
  ok: boolean;

  /** 成功时为对方工具返回的文本，失败时为诊断信息。 */
  text: string;

  /** 失败原因分类；成功时为 undefined。 */
  reason?: string;
}

/** `hello_js_count` 工具的返回结构。 */
export interface CountPayload {
  /** 当前计数。 */
  count: number;

  /** 对方包看到的会话 ID。 */
  sessionId: string;
}

/** 调用其他工具所需的最小宿主能力。 */
export interface ToolCaller {
  /** 调用另一个已注册工具；结果为文本，错误以 JSON 负载返回而不抛出。 */
  executeTool(name: string, argumentsJson: string): Promise<string>;
}

/**
 * 把任意值收窄成可读的字符串，供诊断信息使用。
 * @param value 任意值。
 * @param fallback 值为空时的替代文本。
 * @returns 字符串形式。
 */
export function describeValue(value: unknown, fallback: string): string {
  if (value === null || value === undefined) {
    return fallback;
  }
  const text = String(value);
  return text.length > 0 ? text : fallback;
}

/**
 * 判断工具返回文本是否表示错误。
 *
 * 扩展运行时的工具错误统一以 `{"error": …}` 的形式返回，而不是抛出异常；
 * 因此调用方必须检查文本，否则会把错误当成正常结果继续用。
 * @param text 工具返回的原始文本。
 * @returns 是否为错误负载。
 */
export function looksLikeToolError(text: string): boolean {
  return text.indexOf('"error"') >= 0;
}

/**
 * 解析计数负载。
 *
 * 上游 hello-js 是纯 JavaScript 包，没有类型声明；计数结构由本函数在边界处校验，
 * 因此调用方拿到的是已收窄的类型，而不是 `any`。
 * @param text 工具返回的原始文本。
 * @returns 解析结果；结构不符时返回 undefined。
 */
export function parseCountPayload(text: string): CountPayload | undefined {
  try {
    const parsed = JSON.parse(text) as CountPayload;
    if (parsed === null || typeof parsed !== 'object' || typeof parsed.count !== 'number') {
      return undefined;
    }
    return parsed;
  } catch (error) {
    return undefined;
  }
}

/**
 * 调用另一个扩展包的工具，并把失败折叠成结果而不是抛异常。
 *
 * 失败有三种来源，都会落到 `ok: false`：宿主拒绝调用（抛异常）、
 * 工具返回错误负载、以及返回结构不是预期的计数结构。
 * @param caller 命令上下文（提供 executeTool）。
 * @param toolName 目标工具名。
 * @param argumentsJson 传给目标工具的参数 JSON。
 * @returns 调用结果。
 */
export async function invokeUpstreamTool(
  caller: ToolCaller,
  toolName: string,
  argumentsJson: string
): Promise<CallOutcome> {
  try {
    const text = await caller.executeTool(toolName, argumentsJson);

    if (looksLikeToolError(text)) {
      return { ok: false, text: text, reason: '对方工具返回错误负载' };
    }

    if (parseCountPayload(text) === undefined) {
      return { ok: false, text: text, reason: '返回结果不是预期的计数结构' };
    }

    return { ok: true, text: text };
  } catch (error) {
    // 宿主未加载对方包、工具不可达或沙箱超时都会走到这里。
    return { ok: false, text: describeValue(error, '未知错误'), reason: '调用被宿主拒绝' };
  }
}

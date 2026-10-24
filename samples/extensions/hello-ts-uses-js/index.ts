import { describeValue, invokeUpstreamTool, parseCountPayload } from './lib/contract';

/** 被依赖包 hello-js 提供的工具名。 */
const UpstreamToolName = 'hello_js_count';

/** 本包在会话状态桶里保存的结构。 */
interface BridgeState {
  /** 成功驱动对方工具的次数。 */
  calls: number;

  /** 最近一次失败原因；成功调用后清空。 */
  lastError?: string;
}

/** 宿主命令参数对象。 */
interface CommandArgumentsLike {
  raw: string;
  positional: string[];
  isEmpty: boolean;
}

/** 宿主会话状态存储。 */
interface StateStoreLike {
  get(key: string, fallback: BridgeState): BridgeState;
  set(key: string, value: BridgeState): void;
}

/** 宿主命令上下文中本扩展实际用到的字段。 */
interface CommandContextLike {
  state: StateStoreLike;
  sessionId?: string | null;

  /** 调用另一个已注册工具；结果为文本，错误以 JSON 负载返回而不抛出。 */
  executeTool(name: string, argumentsJson: string): Promise<string>;
}

/** 宿主注册接口中本扩展实际用到的成员。 */
interface ExtensionApiLike {
  registerCommand(
    name: string,
    description: string,
    handler: (context: CommandContextLike, args: CommandArgumentsLike) => Promise<unknown>,
    argumentHint?: string
  ): void;
}

const StateKey = 'bridge';
const EmptyState: BridgeState = { calls: 0 };
const ExtensionName = 'hello-ts-uses-js';

const extension = {
  name: ExtensionName,
  description: '示例扩展：通过 require 依赖 hello-js，并用 executeTool 驱动对方的工具。',

  /**
   * 登记命令。跨包调用发生在命令处理器里，而不是激活期。
   */
  activate(api: ExtensionApiLike): void {
    // 处理器先提成具名常量再登记：扩展转译器不会剥掉「内联箭头函数 + 返回类型注解」的组合，
    // 提出来后注解可以保留，也顺带让契约在函数名处一目了然。
    const handleHelloUsesJs = async (
      context: CommandContextLike,
      args: CommandArgumentsLike
    ): Promise<unknown> => {
      // 三元表达式不写进调用实参：扩展转译器只在实参之外保留完整的 `? :`。
      const increment = args.positional[0] === 'inc';
      const argumentsJson = increment ? '{"increment":true}' : '{}';
      const outcome = await invokeUpstreamTool(context, UpstreamToolName, argumentsJson);

      const stored = context.state.get(StateKey, EmptyState);
      const state: BridgeState = {
        calls: typeof stored.calls === 'number' && Number.isFinite(stored.calls) ? stored.calls : 0,
        lastError: undefined
      };

      if (!outcome.ok) {
        state.lastError = outcome.reason;
        context.state.set(StateKey, state);

        // 失败时返回诊断字符串：宿主会把它当作一条用户消息，而不是让异常中断本轮。
        return (
          '无法调用 ' + UpstreamToolName + '（' + describeValue(outcome.reason, '未知原因') + '）：' +
          outcome.text + '。请确认 hello-js 包已加载，例如把 samples/extensions 目录整体交给宿主扫描。'
        );
      }

      state.calls += 1;
      context.state.set(StateKey, state);

      const payload = parseCountPayload(outcome.text);
      const count = payload === undefined ? 0 : payload.count;
      return {
        display:
          'Hello JS 的计数器为 ' + count + '（本次会话第 ' + state.calls + ' 次跨包调用）。' +
          '用法：`/hello-uses-js inc` 让对方自增。'
      };
    };

    api.registerCommand(
      'hello-uses-js',
      '调用 hello-js 包的 hello_js_count 工具驱动对方的会话计数器',
      handleHelloUsesJs,
      '[inc]'
    );
  }
};

module.exports = extension;

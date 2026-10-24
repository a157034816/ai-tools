import { GreetingStyle, buildGreetingPrompt, describeCount, normalizeName } from './lib/greet';

/** 本扩展在会话状态桶里保存的计数结构。 */
interface HelloState {
  /** 本次会话已经问候的次数。 */
  count: number;

  /** 最近一次问候使用的名字；尚未问候过时为 undefined。 */
  lastGreeting?: string;
}

/** 宿主会话状态存储：按扩展名分桶，值是 JSON。 */
interface StateStoreLike {
  get(key: string, fallback: HelloState): HelloState;
  set(key: string, value: HelloState): void;
}

/** 宿主命令上下文中本扩展实际用到的字段。 */
interface CommandContextLike {
  state: StateStoreLike;
  sessionId?: string | null;
}

/** 宿主命令参数对象。 */
interface CommandArgumentsLike {
  raw: string;
  positional: string[];
  isEmpty: boolean;
}

/** `hello_ts_count` 工具的入参。 */
interface CountToolArguments {
  increment?: boolean;
}

/** `hello_ts_count` 工具的返回值。 */
interface CountToolResult {
  count: number;
  sessionId: string;
}

/** `before_agent_start` 事件中本扩展实际用到的字段。 */
interface BeforeAgentStartLike {
  promptSections: string[];
}

/** 工具参数 Schema。 */
interface CountToolSchema {
  type: string;
  properties: object;
}

/** 工具定义。 */
interface CountToolDefinition {
  name: string;
  description: string;
  parameters: CountToolSchema;
}

/** 工具注册选项。 */
interface ToolRegistrationOptions {
  exposure: string;
  readOnlyHint: boolean;
}

/** 宿主注册接口中本扩展实际用到的成员。 */
interface ExtensionApiLike {
  registerCommand(
    name: string,
    description: string,
    handler: (context: CommandContextLike, args: unknown) => Promise<unknown>,
    argumentHint?: string
  ): void;
  registerTool(
    definition: CountToolDefinition,
    execute: (args: CountToolArguments, context: CommandContextLike) => Promise<CountToolResult>,
    options?: ToolRegistrationOptions
  ): void;
  on(event: string, handler: (event: BeforeAgentStartLike, context: CommandContextLike) => Promise<void>): unknown;
}

const ExtensionName = 'hello-ts';
const CountKey = 'count';
const EmptyState: HelloState = { count: 0 };

/**
 * 读取状态并写回一次更新，返回更新函数的返回值。
 *
 * 计数读写必须在同一处完成，否则命令与工具各自维护一份默认值会漂移。
 */
function updateState<T>(context: CommandContextLike, update: (state: HelloState) => T): T {
  const stored = context.state.get(CountKey, EmptyState);
  const state: HelloState = {
    count: typeof stored.count === 'number' && Number.isFinite(stored.count) ? stored.count : 0,
    lastGreeting: typeof stored.lastGreeting === 'string' ? stored.lastGreeting : undefined
  };
  const result = update(state);
  context.state.set(CountKey, state);
  return result;
}

/**
 * 命令处理器：无参数只显示计数，有参数则把问候提示词发给模型。
 */
async function handleHello(context: CommandContextLike, args: unknown): Promise<unknown> {
  const parsed = args as CommandArgumentsLike;
  const count = updateState(context, (state) => {
    state.count += 1;
    state.lastGreeting = normalizeName(parsed.positional[0]);
    return state.count;
  });

  // 无参数：只显示计数，不触发模型轮次。
  if (parsed.isEmpty) {
    return { display: describeCount(count) };
  }

  // 有参数：返回字符串即作为一条用户消息发给模型。
  const style = count > 1 ? GreetingStyle.Plain : GreetingStyle.Warm;
  const options = { name: parsed.raw, style: style };
  const prompt = buildGreetingPrompt(options, count);
  return prompt;
}

/**
 * 工具处理器：读取或推进计数器。
 */
async function executeCount(args: CountToolArguments, context: CommandContextLike): Promise<CountToolResult> {
  const count = updateState(context, (state) => {
    if (args.increment === true) {
      state.count += 1;
    }
    return state.count;
  });

  const result: CountToolResult = { count, sessionId: context.sessionId ?? '' };
  return result;
}

/**
 * 每轮把计数回灌进系统提示；只读不写，避免触发宿主持久化。
 */
async function appendCounterSection(event: BeforeAgentStartLike, context: CommandContextLike): Promise<void> {
  const stored = context.state.get(CountKey, EmptyState);
  const count = Number.isFinite(stored.count) ? stored.count : 0;
  if (count <= 0) {
    return;
  }

  const section = `## Hello TypeScript\n\n本次会话已问候 **${count}** 次。`;
  if (!event.promptSections.includes(section)) {
    event.promptSections.push(section);
  }
}

const CountToolSchemaValue: CountToolSchema = {
  type: 'object',
  properties: {
    increment: {
      type: 'boolean',
      description: 'Advance the counter by one before returning it.'
    }
  }
};

const CountTool: CountToolDefinition = {
  name: 'hello_ts_count',
  description: 'Reads or advances the hello-ts greeting counter for the current session.',
  parameters: CountToolSchemaValue
};

const CountToolOptions: ToolRegistrationOptions = { exposure: 'direct', readOnlyHint: false };

const extension = {
  name: ExtensionName,
  description: '示例扩展：带类型的会话计数命令、一个工具、一段每轮注入的提示词。',

  /**
   * 登记命令、工具与事件处理器。必须快速返回，不启动进程。
   */
  activate(api: ExtensionApiLike): void {
    api.registerCommand('hello-ts', '向示例扩展问好，并累加本次会话的问候次数', handleHello, '<名字>');
    api.registerTool(CountTool, executeCount, CountToolOptions);
    api.on('before_agent_start', appendCounterSection);
  }
};

module.exports = extension;

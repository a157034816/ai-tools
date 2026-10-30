'use strict';

const { normalizeName, buildGreetingPrompt, describeCount } = require('./lib/greet');

const EXTENSION_NAME = 'hello-js';
const STATE_KEY = 'count';

/**
 * 读取当前计数。状态由宿主按扩展名分桶，跨轮次保留。
 * @param {object} context 命令或工具上下文。
 * @returns {number} 计数。
 */
function readCount(context) {
  const value = context.state.get(STATE_KEY, 0);
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

/**
 * 自增并写回计数。
 * @param {object} context 命令或工具上下文。
 * @returns {number} 自增后的计数。
 */
function bumpCount(context) {
  const next = readCount(context) + 1;
  context.state.set(STATE_KEY, next);
  return next;
}

module.exports = {
  name: EXTENSION_NAME,
  description: '示例扩展：带会话计数的命令、一个工具、一段每轮注入的提示词。',

  /**
   * 登记命令、工具与事件处理器。必须快速返回，不启动进程。
   * @param {object} api 宿主提供的注册接口。
   */
  activate(api) {
    api.registerCommand('hello-js', '向示例扩展问好，并累加本次会话的问候次数', async (context, args) => {
      const count = bumpCount(context);

      // 无参数：只显示计数，不触发模型轮次。
      if (args.isEmpty) {
        return { display: describeCount(count) };
      }

      // 有参数：返回字符串即作为一条用户消息发给模型。
      return buildGreetingPrompt(args.raw, count);
    });

    api.registerTool(
      {
        name: 'hello_js_count',
        description: 'Reads or advances the hello-js greeting counter for the current session.',
        parameters: {
          type: 'object',
          properties: {
            increment: {
              type: 'boolean',
              description: 'Advance the counter by one before returning it.'
            }
          }
        }
      },
      async (args, context) => {
        const count = args && args.increment === true ? bumpCount(context) : readCount(context);
        return { count, sessionId: context.sessionId ?? '' };
      },
      { exposure: 'direct', readOnlyHint: false }
    );

    api.on('before_agent_start', async (event, context) => {
      const count = readCount(context);
      if (count <= 0) {
        return;
      }

      const section = `## Hello JS\n\n本次会话已问候 **${count}** 次。`;
      if (!event.promptSections.includes(section)) {
        event.promptSections.push(section);
      }
    });

    // 除了随包 skills/ 目录，脚本扩展也能在 activate 里登记任意数量的技能。
    // directory 省略时为包根，随包资源以包根为基准解析。
    api.registerSkill({
      name: 'hello-js-session',
      description: '描述 hello-js 本次会话的问候次数，并说明何时使用 hello_js_count 工具。当用户询问问候进度或要延续同一会话风格时加载。',
      instructions: [
        '# Hello JS 会话进度',
        '',
        '本技能在 `activate` 中由 `api.registerSkill` 动态登记。',
        '',
        '1. 读取精确计数：调用 `hello_js_count`，参数 `{}`（只读）。',
        '2. 需要先累加：调用 `hello_js_count`，参数 `{ "increment": true }`。',
        '3. 计数为 0 时按首次问候处理。'
      ].join('\n'),
      allowedTools: ['hello_js_count'],
      metadata: { author: 'AiTools Contributors', origin: 'register-skill' }
    });
  }
};

'use strict';

/**
 * 包内第二个模块：被 index.js 以 `require('./lib/greet')` 引入，
 * 用来证明脚本扩展的模块解析限定在包目录内。
 */

const MAX_NAME_LENGTH = 40;

/**
 * 把用户输入整理成可安全展示的名字。
 * @param {unknown} value 原始输入。
 * @returns {string} 去空白并截断后的名字；空输入返回「世界」。
 */
function normalizeName(value) {
  const text = String(value ?? '').trim().slice(0, MAX_NAME_LENGTH);
  return text.length > 0 ? text : '世界';
}

/**
 * 生成交给模型的问候提示词。
 * @param {unknown} name 名字。
 * @param {number} count 本次会话的问候序号。
 * @returns {string} 提示词文本。
 */
function buildGreetingPrompt(name, count) {
  return `请用一句话向 ${normalizeName(name)} 问好。这是本次会话第 ${count} 次问候。`;
}

/**
 * 生成只显示、不触发模型轮次的说明文本。
 * @param {number} count 当前计数。
 * @returns {string} 展示文本。
 */
function describeCount(count) {
  return `本次会话已问候 ${count} 次。用法：\`/hello-js <名字>\`。`;
}

module.exports = { normalizeName, buildGreetingPrompt, describeCount };

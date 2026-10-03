// llama-server accepts plain Responses function tools, but not Codex's
// namespace containers or hosted web_search tool.
export function normalizeTools(body) {
  const renamedTools = new Map();
  if (!Array.isArray(body.tools)) return renamedTools;

  const tools = [];
  const usedNames = new Set(body.tools.filter((tool) => tool?.type === 'function').map((tool) => tool.name));
  for (const tool of body.tools) {
    if (tool?.type === 'function') {
      tools.push(tool);
    } else if (tool?.type === 'namespace' && Array.isArray(tool.tools)) {
      for (const child of tool.tools) {
        if (child?.type !== 'function') continue;
        const name = `${tool.name}__${child.name}`;
        if (usedNames.has(name)) throw new Error(`Duplicate tool name: ${name}`);
        usedNames.add(name);
        renamedTools.set(name, { namespace: tool.name, name: child.name });
        tools.push({ ...child, name });
      }
    }
  }
  body.tools = tools;
  return renamedTools;
}

export function restoreToolCalls(value, renamedTools) {
  if (!value || typeof value !== 'object') return value;
  if (Array.isArray(value)) {
    for (const item of value) restoreToolCalls(item, renamedTools);
  } else {
    if (value.type === 'function_call' && renamedTools.has(value.name)) {
      const original = renamedTools.get(value.name);
      value.name = original.name;
      value.namespace = original.namespace;
    }
    for (const child of Object.values(value)) restoreToolCalls(child, renamedTools);
  }
  return value;
}

export function restoreSseEvent(event, renamedTools) {
  const lines = event.split(/\r?\n/);
  const dataLines = lines.filter((line) => line.startsWith('data:'));
  if (dataLines.length === 0) return event;
  const data = dataLines.map((line) => line.slice(5).trimStart()).join('\n');
  if (data === '[DONE]') return event;
  let restored;
  try {
    restored = JSON.stringify(restoreToolCalls(JSON.parse(data), renamedTools));
  } catch {
    return event;
  }
  return [...lines.filter((line) => !line.startsWith('data:')), `data: ${restored}`].join('\n');
}

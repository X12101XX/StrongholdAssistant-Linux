import type { AdminMirrorStats, AdminStats, AdminTrafficStats, BotSalesProject, BotSummary } from '../shared/api'
import type { Context } from './auth'
import { fail, iso, json } from './common'
import { mirrorStats } from './mirror'
import { stats } from './stats'
import { trafficStats } from './traffic'

// 当前锁定的类型包尚未声明 Workers 的定长比较扩展。
const subtle = crypto.subtle as SubtleCrypto & { timingSafeEqual(a: ArrayBuffer, b: ArrayBuffer): boolean }
const yuan = (value: number) => Math.round((value + Number.EPSILON) * 100) / 100

export async function bot(ctx: Context, path: string) {
 // TODO(design): 机器人鉴权和方法错误采用简短中文提示，待统一文案。
 if (!ctx.env.BOT_TOKEN) fail(403, 'bot_disabled', '机器人接口未启用。')
 const token = ctx.request.headers.get('Authorization')?.match(/^Bearer ([^\s]+)$/)?.[1]
 const encoder = new TextEncoder()
 const [actual, expected] = await Promise.all([token ?? '', ctx.env.BOT_TOKEN].map(value =>
  crypto.subtle.digest('SHA-256', encoder.encode(value))))
 if (!subtle.timingSafeEqual(actual, expected) || !token) {
  const response = json({ error: 'unauthorized', message: '机器人凭据无效。' }, 401)
  response.headers.set('WWW-Authenticate', 'Bearer')
  return response
 }
 if (ctx.request.method !== 'GET') fail(405, 'method_not_allowed', '只支持 GET 请求。')
 if (path === '/api/bot/stats') return stats(ctx)
 if (path === '/api/bot/mirror') return mirrorStats(ctx)
 if (path === '/api/bot/traffic') return trafficStats(ctx)
 if (path !== '/api/bot/summary') fail(404, 'not_found', '接口不存在。')

 const at = iso(), today = iso(Date.parse(at) + 8 * 3600000).slice(0, 10)
 // 先用销售统计解析月份，再把同一月份交给流量统计，跨北京时间月界也保持一致。
 const mirror = await (await mirrorStats(ctx)).json() as AdminMirrorStats
 const url = new URL(ctx.request.url)
 url.searchParams.set('month', mirror.month)
 const [plaza, traffic] = await Promise.all([
  stats(ctx).then(response => response.json() as Promise<AdminStats>),
  trafficStats({ ...ctx, request: new Request(url, ctx.request) }).then(response => response.json() as Promise<AdminTrafficStats>),
 ])
 const current = mirror.month === today.slice(0, 7)
 const projects: BotSalesProject[] = mirror.projects.map(project => {
  const day = current ? project.daily.find(day => day.date === today) : undefined
  return { rid: project.rid, orders: project.orders, amountYuan: yuan(project.amount),
   incomeYuan: yuan(project.income), refundedOrders: project.refundedOrders,
   refundedAmountYuan: yuan(project.refundedAmount),
   today: current && !project.error ? { orders: day?.orders ?? 0, amountYuan: yuan(day?.amount ?? 0) } : null,
   ...(project.error && { error: project.error }) }
 })
 const successful = mirror.projects.filter(project => !project.error)
 const sum = (key: 'orders' | 'amount' | 'income' | 'refundedOrders' | 'refundedAmount') =>
  successful.reduce((total, project) => total + project[key], 0)
 const days = successful.flatMap(project => project.daily.filter(day => day.date === today))
 const result: BotSummary = { at, month: mirror.month, timezone: 'Asia/Shanghai', currency: 'CNY',
  sales: { projects, complete: successful.length === projects.length,
   total: { orders: sum('orders'), amountYuan: yuan(sum('amount')), incomeYuan: yuan(sum('income')),
    refundedOrders: sum('refundedOrders'), refundedAmountYuan: yuan(sum('refundedAmount')),
    today: current ? { orders: days.reduce((total, day) => total + day.orders, 0),
     amountYuan: yuan(days.reduce((total, day) => total + day.amount, 0)) } : null } },
  plaza: { totals: plaza.totals, today: {
   users: plaza.daily.users.at(-1)!.count, posts: plaza.daily.posts.at(-1)!.count,
   imports: plaza.daily.imports.at(-1)!.count, likes: plaza.daily.likes.at(-1)!.count,
   comments: plaza.daily.comments.at(-1)!.count, incomeGames: plaza.daily.incomeGames.at(-1)!.count,
  } },
  traffic: { bytes: traffic.bytes, costYuan: yuan(traffic.bytes / 1e9 * traffic.pricePerGB),
   forecastBytes: traffic.forecastBytes, forecastCostYuan: yuan(traffic.forecastBytes / 1e9 * traffic.pricePerGB),
   pricePerGB: traffic.pricePerGB, lastReportAt: traffic.lastReportAt },
 }
 return json(result)
}

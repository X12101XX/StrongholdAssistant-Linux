// 我的干员（任务 162、163，设计见 docs/design.md「我的干员：读物资调配页」）：
// 助手在卫戍协议「物资调配」页自己滑动列表，读出每名干员的皮肤、技能、模组、潜能。读到的技能、模组并进「我的默认」
import { api, type Operator, type Roster, type RosterOperator, type RosterScanStatus } from './api'
import { app } from './state.svelte'
import { setMyDefault } from './opconfig'
import { field } from './field.svelte'

type Patch = Partial<Pick<RosterOperator, 'skin' | 'skill' | 'module' | 'potential'>>

const initial = await api.roster()

class RosterState {
  data = $state<Roster>(initial)
  /** 最近一次读取的进度；没读过为 null */
  scan = $state<RosterScanStatus | null>(null)
  /** 点了读取、宿主还没答复（在认页面） */
  starting = $state(false)
  running = $derived(!!this.scan?.running)
  byName = $derived(new Map(this.data.operators.map((o) => [o.name, o])))
  /** 有项没认出来的干员 */
  incomplete = $derived(this.data.operators.filter((o) => o.skin === null || o.skill === null || o.module === null || o.potential === null))
  private startedAt = ''
  /** 盖在画面上时为了不挡列表收起的，读完自动展开（同持续识别） */
  private autoCollapsed = false

  constructor() {
    api.onRoster((r) => (this.data = r))
    api.onRosterScan((s) => {
      this.scan = s
      if (!s.running) this.finish(s)
    })
  }

  async start() {
    if (this.starting || this.running) return
    this.starting = true
    this.startedAt = new Date().toISOString()
    try {
      await api.startRosterScan()
      if (app.connection.overlay && !app.workbench && !app.collapsed) {
        this.autoCollapsed = true
        app.setCollapsed(app.settings.collapseMode)
      }
    } catch (e) {
      app.showToast((e as Error).message.split('\n')[0], true)
    } finally {
      this.starting = false
    }
  }

  stop() {
    api.stopRosterScan()
  }

  private finish(s: RosterScanStatus) {
    if (this.autoCollapsed && app.collapsed) app.setCollapsed(false)
    this.autoCollapsed = false
    const n = this.mergeDefaults(this.data.operators.filter((o) => o.readAt >= this.startedAt))
    // 失败时第二行是原因，接在后面一起显示（原来只显示「读取失败。」，看不出为什么）
    const msg = (s.message ?? '').split('\n').filter(Boolean).join(' ')
    app.showToast(n ? `${msg}；我的默认更新了 ${n} 名` : msg, s.reason === 'failed')
  }

  /** 把读到的技能、模组并进我的默认，返回改了几名 */
  private mergeDefaults(list: RosterOperator[]) {
    let my = app.settings.operatorDefaults
    let n = 0
    for (const r of list) {
      const op = app.op(r.name)
      if (!op.skills?.length) continue
      const patch = { ...(r.skill && { skill: r.skill }), ...(r.module !== null && op.modules?.length && { module: r.module }) }
      if (!Object.keys(patch).length) continue
      const next = setMyDefault(my, op, patch)
      if (JSON.stringify(next[op.name]) !== JSON.stringify(my[op.name])) n++
      my = next
    }
    if (n) {
      app.settings.operatorDefaults = my
      app.saveSettings()
    }
    return n
  }

  /** 图鉴详情里手动改；改技能、模组时我的默认跟着改 */
  async edit(op: Operator, patch: Patch) {
    this.data = await api.setRosterOperator(op.name, patch)
    if ('skill' in patch || 'module' in patch) this.mergeDefaults([this.byName.get(op.name)!])
  }
}

export const roster = new RosterState()


/**
 * 本局可能出现的自选干员（docs/design.md「本局盟约里的自选干员」）：本局读到「确定自选编队」（157）就是本局选的人；
 * 没读到但读过物资调配页，就是自选编队里的人（编队 0 人就是一个也没有）；都没有为 null，不收窄（全部自选干员）。
 * 场上认到的自选干员（本局名单里的）总是算进去
 */
export function diyPool(): { names: Set<string>; from: 'match' | 'squad' } | null {
  const seen = (field.state?.roster ?? []).map((r) => r.operator).filter((n) => app.op(n).diy)
  const p = app.match.customPicks
  if (p) return { names: new Set([...p[5], ...p[6], ...seen]), from: 'match' }
  if (roster.data.readAt) return { names: new Set([...roster.data.customSquad, ...seen]), from: 'squad' }
  return null
}

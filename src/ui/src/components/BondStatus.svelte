<script lang="ts">
  import { slide } from 'svelte/transition'
  import { cubicOut } from 'svelte/easing'
  import { app } from '../lib/state.svelte'
  import { asset, ROMAN } from '../lib/assets'
  import { field } from '../lib/field.svelte'
  import type { FieldUnit } from '../lib/api/types'
  import SectionHeader from './SectionHeader.svelte'
  import BondIcon from './BondIcon.svelte'
  import OpTile from './OpTile.svelte'

  // 本局盟约状态（设计见 docs/design.md「盟约信息显示」）：激活了哪些、里面有谁、当前层数。
  // 有谁：场上认人（FieldState.units）按干员的盟约归组；人数按盟约门槛算档位。
  // 档位和层数：游戏顶栏读到的（MatchInfo.covenantBar）以本回合读到的为准，没读到就用人数算的档位
  // 场上状态用小棋盘的共享模块（field.svelte.ts）
  const fs = $derived(field.state)

  const m = $derived(app.match)
  const round = $derived(m.economy?.round ?? null)
  const bar = $derived(new Map((m.covenantBar ?? []).map((b) => [b.id, b])))
  // 「整备区的【奇迹】干员也可用于激活盟约」这类：整备区的人也算人数
  const benchCounts = (id: string) => app.cov(id).effects.some((e) => e.text.includes('整备区') && e.text.includes('激活盟约'))

  interface Member {
    name: string
    bench: boolean
    status: FieldUnit['status']
  }
  const rows = $derived.by(() => {
    const groups = new Map<string, Map<string, Member>>()
    for (const u of fs?.units ?? []) {
      if (!u.operator) continue
      const op = app.op(u.operator)
      for (const id of [...op.core, ...op.extra]) {
        const g = groups.get(id) ?? groups.set(id, new Map()).get(id)!
        const had = g.get(u.operator)
        // 同一个人场上、整备区都有时算场上的；确定的优先于待确认的
        if (!had || (had.bench && u.zone === 'board') || (had.status !== 'sure' && u.status === 'sure'))
          g.set(u.operator, { name: u.operator, bench: u.zone === 'bench', status: u.status })
      }
    }
    for (const b of m.covenantBar ?? []) if (b.level && !groups.has(b.id)) groups.set(b.id, new Map())
    return [...groups]
      .map(([id, g]) => {
        const cov = app.cov(id)
        const members = [...g.values()].sort((a, b) => +a.bench - +b.bench || app.op(b.name).tier - app.op(a.name).tier)
        const count = members.filter((x) => !x.bench || benchCounts(id)).length
        const next = cov.thresholds.find((t) => count < t) ?? null
        const b = bar.get(id)
        // 顶栏是本回合读到的才算数；回合不知道时有就算
        const fresh = !!b && (round === null || b.round === null || b.round === round)
        const level = fresh && b!.level !== null ? b!.level : cov.thresholds.filter((t) => count >= t).length
        return { id, cov, members, count, next, level, bar: b, fresh, unsure: members.some((x) => x.status === 'pending' || x.status === 'auto') }
      })
      .sort((a, b) => b.level - a.level || b.count - a.count || +(b.cov.kind === 'core') - +(a.cov.kind === 'core'))
  })
  const on = $derived(rows.filter((r) => r.level > 0))
  const off = $derived(rows.filter((r) => r.level === 0))
  const active = $derived(on.length)
  const unknown = $derived(fs?.units.filter((u) => !u.operator).length ?? 0)
  // 顶栏最近一次读到的回合，写在底部说明里
  const barRound = $derived(Math.max(0, ...(m.covenantBar ?? []).map((b) => b.round ?? 0)))

  const open = $derived(!app.folded['match.bonds'])
  const sl = { duration: 200, easing: cubicOut }
  const memberTitle = (x: Member) =>
    `${x.name}${x.bench ? '（整备区）' : ''}${x.status === 'pending' ? '：还没确认是谁，在游戏覆盖层或小棋盘上点一下确认' : x.status === 'auto' ? '：小人比对认的' : ''}`
</script>

<section>
  <SectionHeader title="盟约状态" en="COVENANT STATUS" tag={fs ? `${active} 个激活` : undefined} ontoggle={() => app.toggleFold('match.bonds')} folded={!open}>
    {#snippet icon()}
      <img src={asset.stage('img_title_icon_temp_bond')} alt="" class="ti" />
    {/snippet}
  </SectionHeader>
  {#if open}
    <div class="bonds" transition:slide={sl}>
      {#if !rows.length}
        <p class="empty">{fs ? '场上还没有认出的干员。' : '进了对局、休整期看到棋盘后，这里列出激活的盟约和里面的人。'}</p>
      {/if}
      {#each on as r (r.id)}
        <div class="row">
          <BondIcon id={r.id} size={34} on progress={r.next ? r.count / r.next : undefined} onclick={() => (app.detail = { kind: 'covenant', id: r.id })} />
          <div class="body">
            <div class="top">
              <b class="nm">{r.cov.name}</b>
              <span class="lv term" title="激活到第 {r.level} 档{r.fresh ? '（游戏顶栏读到的）' : ''}">{ROMAN[r.level]}</span>
              <!-- 绝技这类按精锐状态算的、或场上没认出人的，只看顶栏，不写人数 -->
              {#if r.members.length}
                <span class="cnt" title="{benchCounts(r.id) ? '场上和整备区' : '场上'} {r.count} 人{r.next ? `，${r.next} 人到下一档` : ''}">
                  <b class="term">{r.count}</b>{#if r.next}<i class="term">/{r.next}</i>{:else}<i>人</i>{/if}
                </span>
              {/if}
              <span class="spacer"></span>
              {#if r.bar && r.bar.layers !== null}
                <span class="layers" class:stale={!r.fresh} title={r.fresh ? '当前层数（游戏顶栏）' : `第 ${r.bar.round} 回合读到的层数`}>
                  {#if !r.fresh}<em>R{r.bar.round}</em>{/if}<b class="term">{r.bar.layers}</b><i>层</i>
                </span>
              {/if}
            </div>
            {#if r.members.length}
              <div class="mem">
                {#each r.members as x (x.name)}
                  <span class="m" class:bench={x.bench} class:pending={x.status === 'pending'} title={memberTitle(x)}>
                    <OpTile name={x.name} size={28} showName={false} banLook={false} onclick={() => (app.detail = { kind: 'operator', name: x.name })} />
                    {#if x.status === 'pending'}<i class="q">?</i>{/if}
                  </span>
                {/each}
              </div>
            {/if}
          </div>
        </div>
      {/each}
      {#if off.length}
        <div class="off">
          <span class="offh">未激活</span>
          {#each off as r (r.id)}
            <button class="chip" title="{r.cov.name}：{r.count} 人，{r.next} 人激活。{r.members.map((x) => x.name).join('、')}" onclick={() => (app.detail = { kind: 'covenant', id: r.id })}>
              <span class="g" style:--src="url({asset.bond(r.id)})"></span>{r.cov.name}<b class="term">{r.count}</b><i class="term">/{r.next}</i>
            </button>
          {/each}
        </div>
      {/if}
      {#if fs && (unknown || rows.some((r) => r.unsure) || barRound)}
        <p class="foot">
          {#if unknown}有 {unknown} 格还没认出是谁，没算进去。{/if}
          {#if rows.some((r) => r.unsure)}带 ? 的是待确认的人。{/if}
          {#if barRound}层数来自游戏顶栏{barRound !== round ? `，最近一次是第 ${barRound} 回合读到的` : ''}。{/if}
        </p>
      {/if}
    </div>
  {/if}
</section>

<style>
  .ti {
    width: 18px;
    height: 18px;
  }
  .bonds {
    display: flex;
    flex-direction: column;
    gap: 2px;
    padding-top: 6px;
  }
  .row {
    display: flex;
    gap: 8px;
    align-items: flex-start;
    padding: 5px 6px;
    background: rgb(var(--mint-rgb) / 0.05);
    box-shadow: inset 2px 0 0 var(--mint);
  }
  .body {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 3px;
  }
  .top {
    display: flex;
    align-items: baseline;
    gap: 6px;
    min-height: 20px;
    white-space: nowrap;
  }
  .nm {
    font-size: var(--fs-md);
    color: var(--fg-1);
  }
  .lv {
    font-size: var(--fs-xs);
    padding: 0 4px;
    color: var(--mint-ink);
    background: var(--mint);
  }
  .cnt {
    font-size: var(--fs-sm);
    color: var(--fg-2);
  }
  .cnt b {
    font-size: var(--fs-base);
    color: var(--fg-1);
  }
  .cnt i {
    font-style: normal;
    color: var(--fg-3);
  }
  .off {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px;
    padding: 6px 6px 2px;
  }
  .offh {
    font-size: var(--fs-xs);
    color: var(--fg-4);
    margin-right: 2px;
  }
  .chip {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 1px 6px 1px 2px;
    font-size: var(--fs-sm);
    color: var(--fg-3);
    box-shadow: inset 0 0 0 1px var(--line);
  }
  .g {
    width: 14px;
    height: 14px;
    background: currentColor;
    mask: var(--src) center / contain no-repeat;
  }
  .chip:hover {
    color: var(--fg-1);
    box-shadow: inset 0 0 0 1px var(--fg-3);
  }
  .chip b {
    font-weight: normal;
    color: var(--fg-2);
    margin-left: 2px;
  }
  .chip i {
    font-style: normal;
    color: var(--fg-4);
  }
  .spacer {
    flex: 1;
  }
  .layers {
    display: flex;
    align-items: baseline;
    gap: 2px;
    color: var(--fg-3);
    font-size: var(--fs-sm);
  }
  .layers b {
    font-size: var(--fs-2xl);
    line-height: 1;
    color: var(--mint);
  }
  .layers i {
    font-style: normal;
  }
  .layers.stale b {
    color: var(--fg-2);
  }
  .layers em {
    font-style: normal;
    font-size: var(--fs-2xs);
    margin-right: 3px;
    color: var(--fg-4);
  }
  .mem {
    display: flex;
    flex-wrap: wrap;
    gap: 3px;
  }
  .m {
    position: relative;
  }
  .m.bench {
    opacity: 0.5;
  }
  .m.pending :global(img) {
    outline: 1px dashed var(--gold);
    outline-offset: -1px;
  }
  .q {
    position: absolute;
    right: -2px;
    top: -3px;
    font: normal var(--fw-bold) var(--fs-2xs) / 12px var(--font-term);
    width: 12px;
    text-align: center;
    color: var(--bg-0);
    background: var(--gold);
    border-radius: var(--r-pill);
    pointer-events: none;
  }
  .empty,
  .foot {
    margin: 2px 6px;
    font-size: var(--fs-sm);
    line-height: 1.5;
    color: var(--fg-3);
  }
</style>

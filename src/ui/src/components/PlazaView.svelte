<script lang="ts">
  import { app } from '../lib/state.svelte'
  import { api } from '../lib/api'
  import { plaza, postToLineup, lineupKey, PLAZA, type Post } from '../lib/plaza.svelte'
  import { plazaPostMenu } from '../lib/menu.svelte'
  import LineupCard from './LineupCard.svelte'
  import Icon from './Icon.svelte'
  import BondIcon from './BondIcon.svelte'
  import BondPick from './BondPick.svelte'
  import StrategyGrid from './StrategyGrid.svelte'
  import { asset } from '../lib/assets'

  // 阵容页的「广场」分段：网站上的阵容。默认按本局排（识别过本局时）：目标盟约阵容不完整的、名单被禁多的往后；
  // 也能切热门 / 最新 / 精选 / 收藏（v0.7.0，要登录）。点标题看详情、导入
  type Sort = 'match' | 'hot' | 'new' | 'featured' | 'favorite'
  const hasMatch = $derived(app.match.source !== 'none')
  let sort = $state<Sort>(app.match.source !== 'none' ? 'match' : 'hot')
  let posts = $state<Post[]>([])
  let cursor = $state<string | null>(null)
  let loading = $state(false)
  let error = $state('')
  let seq = 0

  // 搜索和筛选（v0.9.1）：搜索框打字停 0.3 秒再查；筛选面板里点盟约（多选要全部包含）、选策略
  const f = plaza.filter
  let typed = $state(f.q)
  let timer: ReturnType<typeof setTimeout> | undefined
  const typing = (v: string) => {
    typed = v
    clearTimeout(timer)
    timer = setTimeout(() => (f.q = v.trim()), 300)
  }
  const clearSearch = () => {
    clearTimeout(timer)
    typed = f.q = ''
  }
  let panel = $state(false)
  const count = $derived(f.covenants.length + (f.strategy ? 1 : 0) + (f.author ? 1 : 0))
  const cov = (id: string) => app.catalog.covenants.find((c) => c.id === id)
  const toggleCov = (id: string) => (f.covenants = f.covenants.includes(id) ? f.covenants.filter((x) => x !== id) : [...f.covenants, id])
  // 目标阵容当前阶段的目标盟约：一键填进筛选
  const targetCovs = $derived(app.active?.goals.map((g) => g.id) ?? [])
  const sameAsTarget = $derived(targetCovs.length > 0 && targetCovs.length === f.covenants.length && targetCovs.every((id) => f.covenants.includes(id)))
  let sq = $state('')
  let pickingSt = $state(false)
  const pickSt = (id: string) => {
    f.strategy = id === f.strategy ? null : id
    pickingSt = false
    sq = ''
  }
  const strat = $derived(app.catalog.strategies.find((s) => s.id === f.strategy))
  const clearFilters = () => {
    f.covenants = []
    f.strategy = null
    f.author = null
  }

  // 先显示上次的列表（v0.9.3）：同样的排序和筛选，第一页存在本机，打开时先摆出来，后台再刷新
  const CACHE = 'sa_plaza_lists'
  type Cached = Record<string, { posts: Post[]; cursor: string | null; at: number }>
  const readCache = (): Cached => {
    try {
      return JSON.parse(localStorage.getItem(CACHE) ?? '{}')
    } catch {
      return {}
    }
  }
  function writeCache(key: string, posts: Post[], cursor: string | null) {
    const all = readCache()
    all[key] = { posts, cursor, at: Date.now() }
    // 只留最近用过的 6 种
    const keep = Object.entries(all).sort((a, b) => b[1].at - a[1].at).slice(0, 6)
    try {
      localStorage.setItem(CACHE, JSON.stringify(Object.fromEntries(keep)))
    } catch {
      /* 存不下就算了 */
    }
  }

  async function load(more = false) {
    const my = ++seq
    // 收藏要登录：没登录时不请求，列表里给登录按钮
    if (sort === 'favorite' && !plaza.me) {
      posts = []
      cursor = null
      return
    }
    // 收藏跟账号走，换账号会串，不缓存
    const key = sort === 'favorite' ? '' : JSON.stringify([sort === 'match' ? 'hot' : sort, f.q, [...f.covenants].sort(), f.strategy, f.author?.id ?? null])
    if (!more && key) {
      const c = readCache()[key]
      if (c) {
        posts = c.posts
        cursor = c.cursor
      }
    }
    loading = true
    error = ''
    try {
      const r = await plaza.list({ sort: sort === 'match' ? 'hot' : sort, q: f.q, covenant: f.covenants, strategy: f.strategy, author: f.author?.id, cursor: more ? cursor : null })
      if (my !== seq) return
      posts = more ? [...posts, ...r.posts] : r.posts
      cursor = r.cursor
      if (!more && key) writeCache(key, r.posts, r.cursor)
    } catch (e) {
      if (my === seq) error = (e as Error).message
    } finally {
      if (my === seq) loading = false
    }
  }
  $effect(() => {
    void sort
    void plaza.me
    void [f.q, f.covenants, f.strategy, f.author]
    load()
  })
  $effect(() => {
    plaza.check()
  })

  // 本局排序在本地算：只看当期能导入的，按阵容适配度（不完整盟约 × 100 + 被禁人数）排，热度次之
  const shown = $derived(
    sort === 'match'
      ? posts
          .filter((p) => p.current)
          .map((p) => ({ p, pen: app.lineupPenalty(postToLineup(p)) }))
          .sort((a, b) => a.pen - b.pen)
          .map((x) => x.p)
      : posts,
  )

  // 卡上直接导入（v0.9.4）：导入后留在列表，按钮变「已导入」，可以连着挑几套。
  // 「已导入」看的是我的阵容里有没有同样内容的（lineupKey），以前导过的、换台电脑同步来的也算
  const mineKeys = $derived(new Set(app.lineups.map(lineupKey)))
  const owned = (p: Post) => mineKeys.has(lineupKey(postToLineup(p)))
  const count1 = (p: Post) =>
    plaza
      .imported(p.id)
      .then((r) => (p.imports = r.imports))
      .catch(() => {})
  const importOne = async (p: Post) => {
    if (await app.importMany([postToLineup(p)])) {
      app.showToast(`已导入「${p.lineup.name}」`)
      count1(p)
    }
  }

  // 多选批量导入：点「多选」进选择模式，点卡片勾选，底部「导入 N 套」。勾的按帖子存，换搜索、换筛选后还在，可以搜几次一起导
  let picking = $state(false)
  let picked = $state<Record<string, Post>>({})
  const pickedN = $derived(Object.keys(picked).length)
  const pickable = (p: Post) => p.current && !owned(p)
  const toggle = (p: Post) => {
    if (!pickable(p)) return
    const { [p.id]: had, ...rest } = picked
    picked = had ? rest : { ...picked, [p.id]: p }
  }
  const allShown = $derived(shown.filter(pickable))
  const allOn = $derived(allShown.length > 0 && allShown.every((p) => picked[p.id]))
  const pickAll = () => (picked = allOn ? Object.fromEntries(Object.entries(picked).filter(([id]) => !allShown.some((p) => p.id === id))) : { ...picked, ...Object.fromEntries(allShown.map((p) => [p.id, p])) })
  const stopPicking = () => {
    picking = false
    picked = {}
  }
  let batching = $state(false)
  async function importPicked() {
    // 勾完之后才变成「已有」的（另一套内容一样、刚在别处导入过）跳过，跟结果一起说
    const keys = new Set(mineKeys)
    const go: Post[] = []
    const skip: Post[] = []
    for (const p of Object.values(picked)) {
      const k = lineupKey(postToLineup(p))
      ;(keys.has(k) ? skip : go).push(p)
      keys.add(k)
    }
    batching = true
    const n = await app.importMany(go.map(postToLineup))
    batching = false
    go.slice(0, n).forEach(count1)
    // 中途出错（提示已经弹了）：导好的去掉勾，没导的留着，可以再点一次
    if (n < go.length) return void (picked = Object.fromEntries(go.slice(n).map((p) => [p.id, p])))
    const names = (ps: Post[]) => (ps.length <= 2 ? ps.map((p) => `「${p.lineup.name}」`).join('') : `${ps.length} 套`)
    app.showToast((n ? `已导入 ${n} 套到我的阵容` : '没有导入') + (skip.length ? `；${names(skip)}我的阵容里已经有了，跳过` : ''))
    stopPicking()
  }

  const sorts: { v: Sort; label: string }[] = [
    { v: 'match', label: '本局' },
    { v: 'hot', label: '热门' },
    { v: 'new', label: '最新' },
    { v: 'featured', label: '精选' },
    { v: 'favorite', label: '收藏' },
  ]
</script>

<div class="search">
  <Icon name="search" size={14} />
  <input type="text" placeholder="搜阵容名、干员、装备、作者" value={typed} oninput={(e) => typing(e.currentTarget.value)} onkeydown={(e) => e.key === 'Escape' && typed && (e.stopPropagation(), clearSearch())} />
  {#if typed}<button class="x" title="清空搜索" aria-label="清空搜索" onclick={clearSearch}><Icon name="close" size={13} /></button>{/if}
</div>
<div class="bar">
  <div class="sorts">
    {#each sorts as s (s.v)}
      <button class:on={sort === s.v} disabled={s.v === 'match' && !hasMatch} title={s.v === 'match' && !hasMatch ? '还没识别本局，识别后按本局可用排序' : ''} onclick={() => (sort = s.v)}>{s.label}</button>
    {/each}
  </div>
  <div class="tools">
  <button class="fbtn" class:on={panel} class:has={count > 0} aria-expanded={panel} onclick={() => (panel = !panel)}>
    筛选{#if count}<em>{count}</em>{/if}<span class="chev"><Icon name="arrowDown" size={12} /></span>
  </button>
  <button class="fbtn pickbtn" class:on={picking} aria-pressed={picking} onclick={() => (picking ? stopPicking() : (picking = true))} title="勾选几套，一次导入">
    {picking ? '退出多选' : '多选'}
  </button>
  <button class="web" onclick={() => api.openUrl(PLAZA)} title="在浏览器里打开阵容广场">网站<Icon name="arrowRight" size={12} /></button>
  </div>
</div>

{#if panel}
  <div class="fpanel">
    <div class="fh">
      <span>盟约<em>选多个时要全部包含</em></span>
      {#if targetCovs.length && !sameAsTarget}<button class="link" onclick={() => (f.covenants = [...targetCovs])}>用目标阵容的</button>{/if}
    </div>
    <BondPick state={(id) => ({ on: f.covenants.includes(id) })} onpick={toggleCov} />
    <!-- 策略：和本局页选策略一样，点开是策略网格（可搜），点头像选上并收起；再点已选的那个取消 -->
    <div class="fh"><span>策略</span></div>
    {#if pickingSt}
      <div class="sph">
        <input bind:value={sq} placeholder="搜名字、发起者、效果" />
        <button class="link" onclick={() => ((pickingSt = false), (sq = ''))}>收起</button>
      </div>
      <StrategyGrid selected={f.strategy} q={sq} onpick={pickSt} />
    {:else}
      <div class="chips">
        <button class="stbtn" class:on={!!strat} onclick={() => (pickingSt = true)} title="点一下选策略">
          {#if strat}<img src={asset.strategy(strat.id)} alt="" /><b>{strat.name}</b><span>{strat.initiator}</span>{:else}<span>全部策略</span>{/if}
          <span class="chev"><Icon name="arrowDown" size={12} /></span>
        </button>
        {#if strat}<button class="link" onclick={() => (f.strategy = null)}>不限策略</button>{/if}
      </div>
    {/if}
    {#if count}<button class="link clear" onclick={clearFilters}>清除筛选</button>{/if}
  </div>
{:else if count}
  <!-- 面板收起时，已选的筛选排成一行，点 × 去掉 -->
  <div class="chips active">
    {#each f.covenants as id (id)}
      <button class="chip" title="去掉这个盟约" onclick={() => toggleCov(id)}><BondIcon {id} size={16} on />{cov(id)?.name}<Icon name="close" size={11} /></button>
    {/each}
    {#if strat}
      <button class="chip" title="取消策略筛选" onclick={() => (f.strategy = null)}><img src={asset.strategy(strat.id)} alt="" />{strat.name}<Icon name="close" size={11} /></button>
    {/if}
    {#if f.author}
      <button class="chip" title="不再只看这个作者" onclick={() => (f.author = null)}>作者：{f.author.nickname}<Icon name="close" size={11} /></button>
    {/if}
    <button class="link" onclick={clearFilters}>清除</button>
  </div>
{/if}
<p class="acct">
  {#if plaza.me}
    已登录：<b>{plaza.me.nickname ?? '（还没起昵称）'}</b>。在「我的阵容」里右键阵容卡「发布到广场」。
    <button class="msg" onclick={() => (plaza.noticesOpen = true)}><Icon name="bell" size={12} />消息{#if plaza.unread}<em>{plaza.unread > 99 ? '99+' : plaza.unread}</em>{/if}</button>
  {:else}
    浏览不用登录；发布、点赞要登录，在设置里或发布时登录。
  {/if}
</p>

{#if sort === 'favorite' && !plaza.me}
  <div class="state"><p>收藏跟着广场账号走，登录后在这里看收藏的阵容。</p><button class="retry" disabled={plaza.loggingIn} onclick={() => plaza.login().catch((e) => app.showToast(e.message, true))}>{plaza.loggingIn ? '等待登录…' : '登录'}</button></div>
{:else if error && !posts.length}
  <div class="state"><p>{error}</p><button class="retry" onclick={() => load()}>重试</button></div>
{:else if !shown.length && !loading}
  <div class="state"><p>{f.q || count ? '没有符合条件的阵容，换个关键词或筛选试试。' : sort === 'favorite' ? '还没有收藏。打开广场里的阵容，点「收藏」就会出现在这里。' : sort === 'featured' ? '还没有精选阵容。' : '广场里还没有阵容。在「我的阵容」里右键阵容卡「发布到广场」，做第一个分享的人。'}</p></div>
{:else}
  {#if error}<p class="stale">{error} 下面是上次的结果。<button class="link" onclick={() => load()}>重试</button></p>{/if}
  {#if picking}<p class="pickhint">点卡片勾选；已导入的、旧赛季的不能勾。换搜索、换筛选后勾过的还在。</p>{/if}
  <div class="list" class:picking>
    {#each shown as p (p.id)}
      {@const have = owned(p)}
      {@const on = !!picked[p.id]}
      <!-- 平时点卡片空白处看详情（头像、按钮照旧）；多选时整张卡是一个勾选框：点哪里都是勾 / 取消 -->
      <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
      <div
        class="pk"
        class:on
        class:off={picking && !pickable(p)}
        aria-checked={picking ? on : undefined}
        onclickcapture={picking ? (e) => (e.stopPropagation(), e.preventDefault(), toggle(p)) : undefined}
        onclick={(e) => !(e.target as Element).closest('button') && (plaza.viewing = p)}
      >
        <LineupCard lineup={postToLineup(p)} menu={() => plazaPostMenu(p)} ontitle={() => (plaza.viewing = p)}>
          {#snippet sub()}
            <button class="by" title="只看 {p.author.nickname} 发的阵容" onclick={() => (f.author = p.author)}>{p.author.nickname}</button>
            <span class="num"><Icon name={p.liked ? 'heartFill' : 'heart'} size={11} />{p.likes}</span>
            <span class="num"><Icon name="download" size={11} />{p.imports}</span>
            <span class="num"><Icon name="chat" size={11} />{p.comments}</span>
            {#if p.featured}<span class="feat">精选</span>{/if}
          {/snippet}
          {#snippet actions()}
            {#if !picking}<button class="view" onclick={() => (plaza.viewing = p)} title="看简介、各阶段、评论">详情</button>{/if}
            {#if !p.current}
              <span class="tag" title="这套是旧赛季数据做的，当期导入不了；点标题还能看">旧赛季</span>
            {:else if have}
              <span class="tag" title="我的阵容里已经有这套了；要再导一份用右键菜单">已导入</span>
            {:else if picking}
              <span class="box" aria-hidden="true">{#if on}<Icon name="check" size={14} />{/if}</span>
            {:else}
              <button class="open" onclick={() => importOne(p)} title="导入到我的阵容；右键还能导入并设为目标">导入</button>
            {/if}
          {/snippet}
        </LineupCard>
      </div>
    {/each}
  </div>
  {#if cursor && sort !== 'match'}
    <button class="more" disabled={loading} onclick={() => load(true)}>{loading ? '加载中…' : '加载更多'}</button>
  {/if}
  {#if sort === 'match'}<p class="note">本局排序取热门前 50 套{f.q || count ? '符合条件的' : ''}里当期能用的，按本局盟约和禁用排。</p>{/if}
{/if}
{#if loading && !posts.length}<p class="state muted">加载中…</p>{/if}

{#if picking}
  <!-- 吸在列表底部：已选几套、全选这一页能勾的、导入 -->
  <div class="batch">
    <span class="cnt">已选 <b>{pickedN}</b> 套</span>
    {#if allShown.length}<button class="link" onclick={pickAll}>{allOn ? '取消全选' : '全选'}</button>{/if}
    <span class="sp"></span>
    <button class="secondary" onclick={stopPicking}>取消</button>
    <button class="primary" disabled={!pickedN || batching} onclick={importPicked}>{batching ? '导入中…' : pickedN ? `导入 ${pickedN} 套` : '导入'}</button>
  </div>
{/if}

<style>
  /* 侧栏窄时排不下一行：按钮不压扁（以前字会被挤成竖排），「筛选」「多选」「网站」一组换到下一行 */
  .bar {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px 8px;
    margin: 10px 0 6px;
  }
  .sorts {
    display: flex;
    gap: 2px;
  }
  .tools {
    flex: 1;
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .sorts button,
  .fbtn,
  .web {
    flex: none;
    white-space: nowrap;
  }
  .search {
    display: flex;
    align-items: center;
    gap: 6px;
    height: 32px;
    margin-top: 10px;
    padding: 0 6px 0 10px;
    color: var(--fg-4);
    background: var(--bg-1);
    box-shadow: inset 0 0 0 1px var(--line);
  }
  .search:focus-within {
    color: var(--mint);
    box-shadow: inset 0 0 0 1px var(--mint-lo);
  }
  .search input {
    flex: 1;
    min-width: 0;
    height: 100%;
    font-size: var(--fs-base);
    color: var(--fg-1);
    background: none;
    border: 0;
    outline: none;
    box-shadow: none;
  }
  .search input::placeholder {
    color: var(--fg-4);
  }
  .x {
    display: grid;
    place-items: center;
    width: 22px;
    height: 22px;
    color: var(--fg-3);
  }
  .x:hover {
    color: var(--fg-1);
    background: var(--bg-3);
  }
  .fbtn {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    height: 26px;
    padding: 0 6px 0 10px;
    font-size: var(--fs-base);
    color: var(--fg-2);
    background: var(--bg-2);
  }
  .fbtn:hover,
  .fbtn.on {
    color: var(--fg-1);
    background: var(--bg-3);
  }
  .fbtn.has {
    color: var(--mint);
  }
  .fbtn em {
    font-style: normal;
    font-family: var(--font-term);
    min-width: 16px;
    padding: 0 3px;
    color: var(--mint-ink);
    background: var(--mint);
  }
  .fbtn .chev {
    display: grid;
    transition: transform var(--t-mid) var(--ease-out);
  }
  .fbtn.on .chev {
    transform: rotate(180deg);
  }
  .fpanel {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin-bottom: 10px;
    padding: 8px 10px 10px;
    background: var(--bg-1);
    box-shadow: inset var(--edge) 0 0 var(--mint-lo);
  }
  .fh {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    margin-top: 2px;
    font-size: var(--fs-sm);
    font-weight: var(--fw-bold);
    color: var(--fg-2);
  }
  .fh em {
    margin-left: 6px;
    font-style: normal;
    font-weight: var(--fw-base);
    color: var(--fg-4);
  }
  .sph {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .sph input {
    flex: 1;
    min-width: 0;
    height: 28px;
    padding: 0 8px;
    font-size: var(--fs-base);
    color: var(--fg-1);
    background: var(--bg-0);
    border: 0;
    outline: none;
    box-shadow: inset 0 0 0 1px var(--line);
  }
  .sph input:focus {
    box-shadow: inset 0 0 0 1px var(--mint-lo);
  }
  .sph input::placeholder {
    color: var(--fg-4);
  }
  /* 策略按钮：没选时写「全部策略」，选了显示头像、名字、发起者；点开是策略网格 */
  .stbtn {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    height: 30px;
    padding: 0 6px 0 8px;
    font-size: var(--fs-base);
    color: var(--fg-2);
    background: var(--bg-2);
  }
  .stbtn:hover {
    color: var(--fg-0);
    background: var(--bg-3);
  }
  .stbtn.on {
    padding-left: 3px;
    background: var(--mint-tint);
    box-shadow: inset 0 0 0 1px var(--mint-lo);
  }
  .stbtn img {
    width: 24px;
    height: 24px;
    object-fit: cover;
  }
  .stbtn b {
    color: var(--fg-0);
  }
  .stbtn span {
    font-size: var(--fs-sm);
    color: var(--fg-3);
  }
  .stbtn .chev {
    display: grid;
    color: var(--fg-4);
  }
  .chips {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px;
  }
  .chips.active {
    margin: -2px 0 10px;
  }
  .chip {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    height: 24px;
    padding: 0 6px 0 3px;
    font-size: var(--fs-sm);
    color: var(--fg-1);
    background: var(--mint-tint);
    box-shadow: inset 0 0 0 1px var(--mint-lo);
  }
  .chip img {
    width: 18px;
    height: 18px;
    object-fit: cover;
  }
  .chip :global(svg) {
    color: var(--fg-3);
  }
  .chip:hover :global(svg) {
    color: var(--fg-0);
  }
  .link {
    font-size: var(--fs-sm);
    color: var(--mint);
  }
  .link:hover {
    text-decoration: underline;
    text-underline-offset: 2px;
  }
  .clear {
    align-self: flex-start;
    margin-top: 2px;
  }
  .sorts button {
    height: 26px;
    padding: 0 10px;
    font-size: var(--fs-base);
    color: var(--fg-2);
    background: var(--bg-2);
  }
  .sorts button:hover:not(:disabled, .on) {
    color: var(--fg-1);
  }
  .sorts button.on {
    color: var(--mint-ink);
    background: var(--mint);
    font-weight: var(--fw-bold);
  }
  .sorts button:disabled {
    color: var(--fg-4);
    cursor: default;
  }
  .web {
    margin-left: auto;
    display: inline-flex;
    align-items: center;
    gap: 2px;
    font-size: var(--fs-base);
    color: var(--mint);
  }
  .acct {
    margin: 0 0 10px;
    font-size: var(--fs-sm);
    color: var(--fg-4);
  }
  .acct b {
    color: var(--fg-2);
  }
  .stale {
    margin: 0 0 8px;
    font-size: var(--fs-sm);
    color: var(--fg-3);
  }
  .msg {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    margin-left: 4px;
    font-size: var(--fs-sm);
    color: var(--mint);
    vertical-align: middle;
  }
  .msg:hover {
    color: var(--fg-1);
  }
  .msg em {
    font-style: normal;
    font-family: var(--font-term);
    min-width: 16px;
    padding: 0 3px;
    text-align: center;
    color: var(--mint-ink);
    background: var(--mint);
  }
  .list {
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .by {
    font-size: var(--fs-sm);
    color: var(--fg-3);
  }
  .by:hover {
    color: var(--mint);
    text-decoration: underline;
  }
  .num {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    font-family: var(--font-term);
    font-size: var(--fs-sm);
    color: var(--fg-4);
  }
  .feat {
    font-size: var(--fs-xs);
    padding: 0 4px;
    color: var(--gold-ink);
    background: var(--gold);
  }
  .open {
    flex: none;
    align-self: flex-start;
    height: 24px;
    padding: 0 10px;
    font-size: var(--fs-base);
    color: var(--mint);
    box-shadow: inset 0 0 0 1px var(--mint-lo);
  }
  .open:hover {
    background: var(--mint-tint);
  }
  .state {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 8px;
    padding: 32px 12px;
    text-align: center;
    font-size: var(--fs-base);
    color: var(--fg-3);
  }
  .state p {
    margin: 0;
    line-height: 1.6;
  }
  .retry,
  .more {
    height: 28px;
    padding: 0 12px;
    font-size: var(--fs-base);
    color: var(--fg-2);
    box-shadow: inset 0 0 0 1px var(--line);
  }
  .more {
    display: block;
    margin: 10px auto 0;
  }
  .note {
    margin: 10px 0 0;
    font-size: var(--fs-sm);
    color: var(--fg-4);
  }
  /* 卡上导入、多选 */
  .pickbtn {
    padding: 0 10px;
  }
  .pickbtn.on {
    color: var(--fg-0);
    background: var(--mint-tint);
    box-shadow: inset 0 0 0 1px var(--mint-lo);
  }
  .pickhint {
    margin: -4px 0 8px;
    font-size: var(--fs-sm);
    color: var(--fg-3);
  }
  .pk {
    display: flex;
    flex-direction: column;
    border-radius: var(--r-2, 0);
  }
  .pk > :global(.lc) {
    flex: 1;
  }
  .pk {
    cursor: pointer;
  }
  .pk:not(.off, .on):hover > :global(.lc) {
    box-shadow: inset 0 0 0 1px var(--fg-4);
  }
  .view {
    flex: none;
    align-self: flex-start;
    height: 24px;
    padding: 0 6px;
    font-size: var(--fs-base);
    color: var(--fg-3);
  }
  .view:hover {
    color: var(--fg-0);
  }
  /* 勾上：强调色浅底加描边（不用侧边线，通用风格主题也一样） */
  .pk.on > :global(.lc) {
    background: linear-gradient(var(--mint-tint), var(--mint-tint)), var(--bg-2);
    box-shadow: inset 0 0 0 1px rgb(var(--mint-rgb) / 0.6);
  }
  .pk.off {
    cursor: default;
    opacity: 0.5;
  }
  .box {
    flex: none;
    align-self: flex-start;
    display: grid;
    place-items: center;
    width: 20px;
    height: 20px;
    margin: 2px 4px 0 0;
    color: var(--mint-ink);
    border-radius: var(--r-1, 0);
    box-shadow: inset 0 0 0 1px var(--fg-4);
  }
  .on .box {
    background: var(--mint);
    box-shadow: none;
  }
  .tag {
    flex: none;
    align-self: flex-start;
    display: inline-flex;
    align-items: center;
    height: 24px;
    padding: 0 10px;
    font-size: var(--fs-base);
    color: var(--fg-3);
    border-radius: var(--r-1, 0);
    box-shadow: inset 0 0 0 1px var(--line);
    cursor: default;
  }
  .batch {
    position: sticky;
    bottom: 0;
    z-index: 2;
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 12px;
    padding: 8px 10px;
    background: var(--bg-1);
    border-radius: var(--r-2, 0);
    box-shadow:
      inset 0 0 0 1px var(--line),
      0 -6px 18px rgb(0 0 0 / 0.35);
    -webkit-backdrop-filter: var(--glass-blur, none);
    backdrop-filter: var(--glass-blur, none);
  }
  .cnt {
    font-size: var(--fs-base);
    color: var(--fg-2);
    white-space: nowrap;
  }
  .cnt b {
    font-family: var(--font-term);
    color: var(--mint);
  }
  .sp {
    flex: 1;
  }
  .primary,
  .secondary {
    height: 30px;
    padding: 0 12px;
    font-size: var(--fs-base);
    font-weight: var(--fw-bold);
    white-space: nowrap;
  }
  .primary {
    color: var(--mint-ink);
    background: var(--mint);
  }
  .primary:hover:not(:disabled) {
    background: var(--mint-hi);
  }
  .secondary {
    color: var(--fg-1);
    background: var(--bg-4);
  }
  .primary:disabled {
    color: var(--fg-4);
    background: var(--bg-3);
    cursor: default;
  }
  /* 宽（工作台）：帖子卡网格 */
  @container (min-width: 720px) {
    .list {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(340px, 1fr));
      /* 同一行的卡片一样高 */
      align-items: stretch;
      gap: 8px;
    }
  }
</style>

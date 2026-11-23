<script lang="ts" generics="T extends string | number">
  import { tick } from 'svelte'
  import Icon from './Icon.svelte'

  // 下拉框（v1.0.1）：代替浏览器原生 select，样式同右键菜单。
  // 列表用 fixed 定位，不会被编辑器这类滚动区裁掉；下面放不下就往上弹。键盘：上下选、回车确定、Esc 关
  interface Props {
    value: T
    options: { value: T; label: string; disabled?: boolean }[]
    onchange: (v: T) => void
    /** 按钮最小宽度（px） */
    width?: number
    label?: string
  }
  let { value, options, onchange, width = 44, label }: Props = $props()

  let open = $state(false)
  let btn = $state<HTMLButtonElement>()
  let list = $state<HTMLDivElement>()
  let pos = $state({ x: 0, y: 0, w: 0, up: false })
  let hot = $state(0)
  const cur = $derived(options.find((o) => o.value === value))

  async function show() {
    const r = btn!.getBoundingClientRect()
    const h = Math.min(240, options.length * 26 + 8)
    const up = r.bottom + h + 8 > innerHeight && r.top > h + 8
    pos = { x: r.left, y: up ? r.top - 2 : r.bottom + 2, w: r.width, up }
    hot = Math.max(0, options.findIndex((o) => o.value === value))
    open = true
    await tick()
    list?.querySelector<HTMLElement>('.on')?.scrollIntoView({ block: 'nearest' })
  }
  const pick = (o: Props['options'][number]) => {
    if (o.disabled) return
    open = false
    if (o.value !== value) onchange(o.value)
    btn?.focus()
  }
  function key(e: KeyboardEvent) {
    if (!open) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Enter' || e.key === ' ') (e.preventDefault(), show())
      return
    }
    e.preventDefault()
    if (e.key === 'Escape') return void (open = false)
    if (e.key === 'Enter' || e.key === ' ') return pick(options[hot])
    const d = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : 0
    if (!d) return
    for (let i = hot + d; i >= 0 && i < options.length; i += d)
      if (!options[i].disabled) {
        hot = i
        break
      }
    tick().then(() => list?.children[hot]?.scrollIntoView({ block: 'nearest' }))
  }
  // 列表挂到 body：编辑器这类面板带 transform，里面的 fixed 会按面板定位
  const portal = (node: HTMLElement) => {
    document.body.appendChild(node)
    return { destroy: () => node.remove() }
  }
  // 点外面、滚动、改窗口大小都关掉（fixed 定位跟不上滚动）
  const outside = (e: Event) => open && !btn?.contains(e.target as Node) && !list?.contains(e.target as Node) && (open = false)
</script>

<svelte:window onpointerdown={outside} onresize={() => (open = false)} onscrollcapture={(e) => open && !list?.contains(e.target as Node) && (open = false)} />

<button bind:this={btn} class="sel" class:open style:min-width="{width}px" aria-haspopup="listbox" aria-expanded={open} aria-label={label} onclick={() => (open ? (open = false) : show())} onkeydown={key}>
  <span>{cur?.label ?? ''}</span>
  <Icon name="arrowDown" size={13} />
</button>

{#if open}
  <div
    bind:this={list}
    use:portal
    class="list"
    class:up={pos.up}
    role="listbox"
    style:left="{pos.x}px"
    style:top={pos.up ? undefined : `${pos.y}px`}
    style:bottom={pos.up ? `${innerHeight - pos.y}px` : undefined}
    style:min-width="{pos.w}px"
  >
    {#each options as o, i (o.value)}
      <button class="it" class:on={o.value === value} class:hot={i === hot} role="option" aria-selected={o.value === value} disabled={o.disabled} onpointerenter={() => (hot = i)} onclick={() => pick(o)}>
        <span>{o.label}</span>
        {#if o.value === value}<Icon name="check" size={12} />{/if}
      </button>
    {/each}
  </div>
{/if}

<style>
  .sel {
    display: inline-flex;
    align-items: center;
    justify-content: space-between;
    gap: 4px;
    height: 22px;
    padding: 0 4px 0 8px;
    font-size: var(--fs-md);
    font-family: var(--font-term);
    color: var(--fg-1);
    background: var(--bg-1);
    box-shadow: inset 0 0 0 1px var(--line);
    transition:
      box-shadow var(--t-fast),
      color var(--t-fast);
  }
  .sel :global(svg) {
    color: var(--fg-3);
    transition: transform var(--t-fast);
  }
  .sel:hover,
  .sel:focus-visible,
  .sel.open {
    box-shadow: inset 0 0 0 1px rgb(var(--mint-rgb) / 0.6);
    outline: 0;
  }
  .sel.open :global(svg) {
    transform: rotate(180deg);
    color: var(--mint);
  }
  .list {
    position: fixed;
    z-index: 110;
    max-height: 240px;
    overflow-y: auto;
    padding: 4px 0;
    background:
      var(--noise),
      linear-gradient(180deg, var(--pop-top), var(--pop-bottom));
    box-shadow:
      inset 0 0 0 1px var(--line-strong),
      0 8px 24px rgb(var(--shade-rgb) / 0.6),
      0 0 0 1px rgb(var(--shade-rgb) / 0.5);
    transform-origin: top left;
    animation: pop var(--t-fast) var(--ease-out);
  }
  .list.up {
    transform-origin: bottom left;
  }
  .list::before {
    content: '';
    position: absolute;
    top: 0;
    left: 6px;
    width: 18px;
    height: 2px;
    background: var(--mint);
    box-shadow: var(--mint-glow);
  }
  @keyframes pop {
    from {
      opacity: 0;
      transform: scale(0.96) translateY(-2px);
    }
  }
  .it {
    width: 100%;
    height: 26px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 0 8px 0 10px;
    font-size: var(--fs-md);
    font-family: var(--font-term);
    text-align: left;
    color: var(--fg-2);
  }
  .it.hot {
    color: var(--fg-1);
    background: var(--bg-4);
  }
  .it.on {
    color: var(--mint);
  }
  .it:disabled {
    color: var(--fg-4);
  }
</style>

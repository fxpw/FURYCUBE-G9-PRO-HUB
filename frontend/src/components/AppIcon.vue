<script setup lang="ts">
defineProps<{ name: string; size?: number }>();
</script>
<template>
  <svg
    :width="size || 20"
    :height="size || 20"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.6"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <template v-if="name === 'mouse'">
      <rect x="6" y="2" width="12" height="20" rx="6" />
      <path d="M6 9h12M12 2v7M12 5v1" />
    </template>
    <template v-else-if="name === 'screen'">
      <rect x="2" y="4" width="20" height="14" rx="2" />
      <path d="M8 22h8m-4-4v4M6 8h4m-4 4h8" />
    </template>
    <template v-else-if="name === 'macro'">
      <path d="m13 2-9 12h7l-1 8 10-12h-7z" />
    </template>
    <template v-else-if="name === 'profiles'">
      <rect x="3" y="3" width="14" height="14" rx="2" />
      <path d="M7 21h12a2 2 0 0 0 2-2V7M7 7h6m-6 4h4" />
    </template>
    <template v-else-if="name === 'settings'">
      <path d="m9 3-1 3-3 1-2 5 2 5 3 1 1 3h6l1-3 3-1 2-5-2-5-3-1-1-3z" />
      <circle cx="12" cy="12" r="3" />
    </template>
    <template v-else-if="name === 'refresh'">
      <path d="M20 7V3l-3 3a8 8 0 1 0 3 8M20 7h-5" />
    </template>
    <template v-else-if="name === 'arrow'"><path d="m8 4 8 8-8 8" /></template>
    <template v-else-if="name === 'plus'">
      <path d="M12 5v14M5 12h14" />
    </template>
    <template v-else-if="name === 'save'">
      <path d="M4 3h13l3 3v15H4zM8 3v6h8V3M8 21v-8h8v8" />
    </template>
    <template v-else-if="name === 'upload'">
      <path d="M12 16V3m-5 5 5-5 5 5M4 15v6h16v-6" />
    </template>
    <template v-else-if="name === 'trash'">
      <path d="M3 6h18M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7m4-7v7" />
    </template>
    <template v-else-if="name === 'grip'">
      <path d="M9 5h.01M15 5h.01M9 12h.01M15 12h.01M9 19h.01M15 19h.01" stroke-width="3" />
    </template>
    <template v-else-if="name === 'check'">
      <path d="m5 12 4 4L19 6" />
    </template>
    <template v-else>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 8v5m0 3h.01" />
    </template>
  </svg>
</template>

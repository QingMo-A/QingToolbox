<script setup lang="ts">import{onMounted}from'vue';import{useThemeStore}from'./themeStore';import QSidebarLayout from'../design-system/layouts/QSidebarLayout.vue';import QToast from'../design-system/components/QToast.vue';const theme=useThemeStore();onMounted(()=>theme.set(theme.mode))</script>
<template><QSidebarLayout><router-view/></QSidebarLayout><QToast/></template>

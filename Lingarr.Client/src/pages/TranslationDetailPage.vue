<template>
    <div class="w-full">
        <div class="flex items-center bg-tertiary p-4">
            <ButtonComponent
                variant="ghost"
                size="xs"
                @click="router.push({ name: 'translations' })">
                <ArrowLeft class="ml-1 h-3.5 w-3.5" />
            </ButtonComponent>
        </div>

        <div v-if="loading" class="flex justify-center py-12">
            <LoaderCircleIcon class="text-primary-content/50 h-8 w-8 animate-spin" />
        </div>

        <div v-else-if="detail" class="grid grid-flow-row auto-rows-max grid-cols-1 gap-4 p-4">
            <CardComponent :title="detail.title">
                <template #content>
                    <div class="grid grid-cols-2 gap-x-6 gap-y-3 md:grid-cols-4">
                        <div>
                            <span class="font-semibold">Status</span>
                            <div class="mt-1">
                                <TranslationStatus :translation-status="detail.status" />
                            </div>
                        </div>
                        <div>
                            <span class="font-semibold">Source</span>
                            <div class="mt-1">
                                <BadgeComponent
                                    classes="text-primary-content border-accent bg-secondary">
                                    {{ detail.sourceLanguage.toUpperCase() }}
                                </BadgeComponent>
                            </div>
                        </div>
                        <div>
                            <span class="font-semibold">Target</span>
                            <div class="mt-1">
                                <BadgeComponent
                                    classes="text-primary-content border-accent bg-secondary">
                                    {{ detail.targetLanguage.toUpperCase() }}
                                </BadgeComponent>
                            </div>
                        </div>
                        <div>
                            <span class="font-semibold">Media Type</span>
                            <div class="mt-1 text-sm">{{ detail.mediaType }}</div>
                        </div>
                        <div v-if="detail.createdAt">
                            <span class="font-semibold">Created At</span>
                            <div class="mt-1 text-sm">
                                {{ formatDateTime(detail.createdAt) }}
                            </div>
                        </div>
                        <div v-if="detail.completedAt">
                            <span class="font-semibold">Completed</span>
                            <div class="mt-1 text-sm">
                                {{ formatDateTime(detail.completedAt) }}
                            </div>
                        </div>
                        <div v-if="detail.subtitleToTranslate" class="col-span-2">
                            <span class="font-semibold">Subtitle Path</span>
                            <div class="mt-1 break-all text-sm">
                                {{ detail.subtitleToTranslate }}
                            </div>
                        </div>
                    </div>
                    <div
                        v-if="detail.errorMessage"
                        class="rounded-md border border-red-700/50 bg-red-900/20 p-4">
                        <div class="text-sm text-red-400">{{ detail.errorMessage }}</div>
                        <details v-if="detail.stackTrace" class="mt-3">
                            <summary
                                class="cursor-pointer text-sm text-red-400/70 hover:text-red-400">
                                StackTrace
                            </summary>
                            <pre
                                class="mt-2 whitespace-pre-wrap break-all rounded bg-black/30 p-3 text-xs text-red-300/80"
                                >{{ detail.stackTrace }}</pre
                            >
                        </details>
                    </div>
                    <div v-if="showProgress" class="flex items-center gap-3">
                        <TranslationProgress :progress="displayProgress" />
                        <span class="shrink-0 text-sm font-medium text-primary-content">
                            {{ displayProgress }}%
                        </span>
                    </div>
                </template>
            </CardComponent>

            <CardComponent v-if="detail.retrySummary" title="Selective Retry">
                <template #content>
                    <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
                        <div
                            v-for="metric in retryMetrics"
                            :key="metric.label"
                            class="rounded-md border border-accent/60 bg-primary/30 p-3">
                            <div class="text-xs font-semibold uppercase text-secondary-content">
                                {{ metric.label }}
                            </div>
                            <div class="mt-1 text-2xl font-bold text-primary-content">
                                {{ metric.value }}
                            </div>
                        </div>
                    </div>

                    <div class="grid gap-4 md:grid-cols-2">
                        <div class="rounded-md border border-accent/60 bg-primary/30 p-4">
                            <h3 class="text-sm font-semibold text-primary-content">
                                Reason distribution
                            </h3>
                            <div v-if="retryReasonRows.length > 0" class="mt-3 space-y-2">
                                <div
                                    v-for="reason in retryReasonRows"
                                    :key="reason.key"
                                    class="flex items-center justify-between gap-3 text-sm">
                                    <span class="min-w-0 truncate text-primary-content">
                                        {{ reason.label }}
                                    </span>
                                    <BadgeComponent
                                        classes="border-accent bg-secondary text-primary-content">
                                        {{ reason.count }}
                                    </BadgeComponent>
                                </div>
                            </div>
                            <div v-else class="mt-3 text-sm text-secondary-content">
                                No suspicious reasons were detected.
                            </div>
                        </div>

                        <div class="rounded-md border border-accent/60 bg-primary/30 p-4">
                            <h3 class="text-sm font-semibold text-primary-content">
                                Outcome explanation
                            </h3>
                            <p class="mt-3 text-sm leading-6 text-secondary-content">
                                Selective retry only replaces a line when the quality checks
                                improve. When a retry does not improve the detected issues,
                                Lingarr keeps the previous best translation unchanged.
                            </p>
                        </div>
                    </div>
                </template>
            </CardComponent>

            <CardComponent v-if="reversedLines.length > 0" title="Translated Lines">
                <template #content>
                    <div class="hidden border-b border-accent font-bold md:grid md:grid-cols-12">
                        <div class="col-span-1 px-4 py-2 text-sm">#</div>
                        <div class="col-span-5 px-4 py-2 text-sm">Source</div>
                        <div class="col-span-6 px-4 py-2 text-sm">Target</div>
                    </div>
                    <div class="max-h-96 overflow-y-auto">
                        <div
                            v-for="line in reversedLines"
                            :key="line.position"
                            class="border-accent/30 grid grid-cols-1 gap-1 border-b transition-colors duration-500 last:border-b-0 md:grid-cols-12 md:gap-0"
                            :class="
                                line.position === latestPosition
                                    ? 'bg-accent/10'
                                    : line.position === latestPosition - 1
                                      ? 'bg-accent/5'
                                      : ''
                            ">
                            <div class="px-4 py-2 text-xs text-primary-content md:col-span-1">
                                {{ line.position }}
                            </div>
                            <div class="px-4 py-2 text-sm text-primary-content md:col-span-5">
                                {{ line.source }}
                            </div>
                            <div class="px-4 py-2 text-sm text-primary-content md:col-span-6">
                                {{ line.target }}
                            </div>
                        </div>
                    </div>
                </template>
            </CardComponent>
        </div>

        <div v-else class="text-primary-content/50 py-12 text-center">Not Found</div>
    </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import {
    Hub,
    ITranslationRequestDetail,
    IRequestProgress,
    ILineTranslated,
    TRANSLATION_STATUS
} from '@/ts'
import { useSignalR } from '@/composables/useSignalR'
import { formatDateTime } from '@/utils/date'
import services from '@/services'
import TranslationStatus from '@/components/common/TranslationStatus.vue'
import TranslationProgress from '@/components/common/TranslationProgress.vue'
import BadgeComponent from '@/components/common/BadgeComponent.vue'
import ButtonComponent from '@/components/common/ButtonComponent.vue'
import CardComponent from '@/components/common/CardComponent.vue'
import LoaderCircleIcon from '@/components/icons/LoaderCircleIcon.vue'
import ArrowLeft from '@/components/icons/ArrowLeft.vue'

const props = defineProps<{
    id: string
}>()

const router = useRouter()
const signalR = useSignalR()

const detail = ref<ITranslationRequestDetail | null>(null)
const loading = ref(true)
const progress = ref(0)
const hubConnection = ref<Hub>()
const latestPosition = ref<number>(0)

const reversedLines = computed(() => (detail.value ? [...detail.value.lines].reverse() : []))

const retryMetrics = computed(() => {
    const summary = detail.value?.retrySummary
    if (!summary) {
        return []
    }

    return [
        { label: 'Attempted', value: summary.attempted },
        { label: 'Improved', value: summary.improved },
        { label: 'Failed', value: summary.failed },
        { label: 'Skipped', value: summary.skipped }
    ]
})

const retryReasonRows = computed(() => {
    const reasons = detail.value?.retrySummary?.reasonDistribution ?? {}
    return Object.entries(reasons)
        .map(([key, count]) => ({
            key,
            label: formatRetryReason(key),
            count
        }))
        .sort((left, right) => right.count - left.count || left.label.localeCompare(right.label))
})

function formatRetryReason(reason: string) {
    return reason
        .split('_')
        .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
        .join(' ')
}

const showProgress = computed(() => {
    if (!detail.value) {
        return false
    }
    const status = detail.value.status
    return (
        (status === TRANSLATION_STATUS.INPROGRESS && progress.value > 0) ||
        status === TRANSLATION_STATUS.COMPLETED
    )
})

const displayProgress = computed(() =>
    detail.value?.status === TRANSLATION_STATUS.COMPLETED ? 100 : progress.value
)

async function loadTranslationDetail() {
    detail.value = await services.translationRequest.get<ITranslationRequestDetail>(Number(props.id))
    progress.value = detail.value.progress ?? 0
    if (!detail.value.lines) {
        detail.value.lines = []
    }
}

const handleProgress = (requestProgress: IRequestProgress) => {
    if (detail.value && requestProgress.id === detail.value.id) {
        detail.value.status = requestProgress.status
        progress.value = requestProgress.progress
        if (requestProgress.completedAt) {
            detail.value.completedAt = requestProgress.completedAt
        }
        if (requestProgress.errorMessage) {
            detail.value.errorMessage = requestProgress.errorMessage
        }
        if (requestProgress.stackTrace) {
            detail.value.stackTrace = requestProgress.stackTrace
        }
        if (requestProgress.completed) {
            void loadTranslationDetail().catch(() => undefined)
        }
    }
}

const handleLineTranslated = (line: ILineTranslated) => {
    if (detail.value && line.id === detail.value.id) {
        detail.value.lines.push({
            position: line.position,
            source: line.source,
            target: line.target
        })
        latestPosition.value = line.position
    }
}

onMounted(async () => {
    try {
        await loadTranslationDetail()
    } catch {
        detail.value = null
    } finally {
        loading.value = false
    }

    hubConnection.value = await signalR.connect(
        'TranslationRequests',
        '/signalr/TranslationRequests'
    )
    await hubConnection.value.joinGroup({ group: 'TranslationRequests' })
    hubConnection.value.on('RequestProgress', handleProgress)
    hubConnection.value.on('LineTranslated', handleLineTranslated)
})

onUnmounted(async () => {
    hubConnection.value?.off('RequestProgress', handleProgress)
    hubConnection.value?.off('LineTranslated', handleLineTranslated)
})
</script>

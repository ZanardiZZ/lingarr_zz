<template>
    <CardComponent title="Translation Request">
        <template #description>
            Modify translation request settings by changing retry options or batch size if available in the service.
        </template>
        <template #content>
            <SaveNotification ref="saveNotification" />

            <template
                v-if="
                [
                    SERVICE_TYPE.ANTHROPIC,
                    SERVICE_TYPE.DEEPSEEK,
                    SERVICE_TYPE.GEMINI,
                    SERVICE_TYPE.LOCALAI,
                    SERVICE_TYPE.OPENAI
                ].includes(
                    serviceType as 'openai' | 'anthropic' | 'localai' | 'gemini' | 'deepseek'
                )
            ">
                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Use batch translation</span>
                    Process multiple subtitle lines together in batches to improve translation
                    efficiency and context awareness. Note that single-line translations with context
                    are still more reliable and of higher quality.
                </div>
                <ToggleButton v-model="useBatchTranslation">
                    <span class="text-sm font-medium text-primary-content">
                        {{ useBatchTranslation == 'true' ? 'Enabled' : 'Disabled' }}
                    </span>
                </ToggleButton>
            </template>

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Batch size:</span>
                Amount of subtitle lines in a single batch.
            </div>
            <InputComponent
                v-if="useBatchTranslation == 'true'"
                v-model="maxBatchSize"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                @update:validation="(val) => (isValid.maxBatchSize = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Max translation retries:</span>
                Maximum number of retries per line or batch.
            </div>
            <InputComponent
                v-model="maxRetries"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                @update:validation="(val) => (isValid.maxRetries = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Delay between retries:</span>
                Initial delay before retrying, in seconds.
            </div>
            <InputComponent
                v-model="retryDelay"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                @update:validation="(val) => (isValid.retryDelay = val)" />

            <div class="flex flex-col space-x-2">
                <span class="font-semibold">Retry delay multiplier:</span>
                Factor by which the delay increases after each retry.
            </div>
            <InputComponent
                v-model="retryDelayMultiplier"
                :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                @update:validation="(val) => (isValid.retryDelayMultiplier = val)" />


            <details class="rounded-lg border border-tertiary-content/20 p-3">
                <summary class="cursor-pointer text-sm font-semibold text-primary-content">
                    Selective retry (advanced)
                </summary>

                <div class="mt-3 flex flex-col space-y-3">
                    <div class="flex flex-col">
                        <span class="font-semibold">Enable selective retry</span>
                        Retry only suspicious subtitle cues after the main translation pass.
                    </div>
                    <ToggleButton v-model="selectiveRetryEnabled">
                        <span class="text-sm font-medium text-primary-content">
                            {{ selectiveRetryEnabled == 'true' ? 'Enabled' : 'Disabled' }}
                        </span>
                    </ToggleButton>

                    <div class="flex flex-col">
                        <span class="font-semibold">Max retry attempts per cue (0-2)</span>
                        Limits additional retries for each suspicious cue.
                    </div>
                    <InputComponent
                        v-model="selectiveRetryMaxAttempts"
                        :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                        @update:validation="(val) => (isValid.selectiveRetryMaxAttempts = val)" />

                    <div class="flex flex-col">
                        <span class="font-semibold">Retry high severity only</span>
                        Requires the weighted score to reach the high-severity floor.
                    </div>
                    <ToggleButton v-model="selectiveRetryHighSeverityOnly">
                        <span class="text-sm font-medium text-primary-content">
                            {{ selectiveRetryHighSeverityOnly == 'true' ? 'Enabled' : 'Disabled' }}
                        </span>
                    </ToggleButton>

                    <div class="flex flex-col">
                        <span class="font-semibold">Score threshold (0-200)</span>
                        Minimum analyzer score required before a cue is retried.
                    </div>
                    <InputComponent
                        v-model="selectiveRetryScoreThreshold"
                        :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                        @update:validation="(val) => (isValid.selectiveRetryScoreThreshold = val)" />

                    <div class="flex flex-col">
                        <span class="font-semibold">Improvement margin (0-100)</span>
                        Minimum score improvement required before a retry replaces the original.
                    </div>
                    <InputComponent
                        v-model="selectiveRetryImprovementMargin"
                        :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                        @update:validation="(val) => (isValid.selectiveRetryImprovementMargin = val)" />

                    <div class="flex flex-col">
                        <span class="font-semibold">Provider scope</span>
                        Choose whether retries run only on LLM providers or all providers.
                    </div>
                    <select v-model="selectiveRetryProviderScope" class="select select-bordered w-full">
                        <option value="llm_only">llm_only</option>
                        <option value="all">all</option>
                    </select>
                    <span
                        v-if="selectiveRetryProviderScope === 'all'"
                        class="badge badge-warning w-fit text-xs">
                        Warning: scope "all" can increase cost and processing time
                    </span>

                    <div class="flex flex-col">
                        <span class="font-semibold">Log retry attempts</span>
                        Enables structured retry attempt logging without subtitle text content.
                    </div>
                    <ToggleButton v-model="selectiveRetryLogAttempts">
                        <span class="text-sm font-medium text-primary-content">
                            {{ selectiveRetryLogAttempts == 'true' ? 'Enabled' : 'Disabled' }}
                        </span>
                    </ToggleButton>

                    <div class="flex flex-col">
                        <span class="font-semibold">Glossary map</span>
                        JSON object keyed by language pair, mapping source terms to preferred target terms.
                    </div>
                    <textarea
                        v-model="selectiveRetryGlossary"
                        class="w-full resize-y rounded-md border bg-transparent px-4 py-2 outline-hidden transition-colors duration-200"
                        :class="isValid.selectiveRetryGlossary ? 'border-accent' : 'border-red-500'"
                        rows="5"
                        placeholder='{ "en:pt": { "New York": "Nova York", "Malcolm": "Malcolm" } }'></textarea>
                    <span v-if="!isValid.selectiveRetryGlossary" class="text-sm text-red-600">
                        Glossary must be a JSON object.
                    </span>

                    <div class="flex flex-col">
                        <span class="font-semibold">Proper noun lock mode</span>
                        Preserve detected source names unless the glossary maps them explicitly.
                    </div>
                    <ToggleButton v-model="selectiveRetryProperNounLockEnabled">
                        <span class="text-sm font-medium text-primary-content">
                            {{ selectiveRetryProperNounLockEnabled == 'true' ? 'Enabled' : 'Disabled' }}
                        </span>
                    </ToggleButton>

                    <div class="flex flex-col">
                        <span class="font-semibold">Protected entity patterns</span>
                        JSON array of regex patterns for extra source tokens that must remain unchanged.
                    </div>
                    <textarea
                        v-model="selectiveRetryProtectedPatterns"
                        class="w-full resize-y rounded-md border bg-transparent px-4 py-2 outline-hidden transition-colors duration-200"
                        :class="isValid.selectiveRetryProtectedPatterns ? 'border-accent' : 'border-red-500'"
                        rows="3"
                        placeholder='["\\b[A-Z]{2,}\\b"]'></textarea>
                    <span v-if="!isValid.selectiveRetryProtectedPatterns" class="text-sm text-red-600">
                        Protected patterns must be a JSON array of strings.
                    </span>
                </div>
            </details>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, ref, reactive } from 'vue'
import { useSettingStore } from '@/store/setting'
import { INPUT_VALIDATION_TYPE, SERVICE_TYPE, SETTINGS } from '@/ts'
import CardComponent from '@/components/common/CardComponent.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import ToggleButton from '@/components/common/ToggleButton.vue'

const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const settingsStore = useSettingStore()
const isValid = reactive({
    maxBatchSize: true,
    maxRetries: true,
    retryDelay: true,
    retryDelayMultiplier: true,
    selectiveRetryMaxAttempts: true,
    selectiveRetryScoreThreshold: true,
    selectiveRetryImprovementMargin: true,
    selectiveRetryGlossary: true,
    selectiveRetryProtectedPatterns: true
})
const serviceType = computed(() => settingsStore.getSetting(SETTINGS.SERVICE_TYPE))

const useBatchTranslation = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.USE_BATCH_TRANSLATION) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.USE_BATCH_TRANSLATION, newValue, true)
        saveNotification.value?.show()
    }
})

const maxBatchSize = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MAX_BATCH_SIZE) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MAX_BATCH_SIZE, newValue, isValid.maxBatchSize)
        saveNotification.value?.show()
    }
})

const maxRetries = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MAX_RETRIES) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MAX_RETRIES, newValue, isValid.maxRetries)
        saveNotification.value?.show()
    }
})

const retryDelay = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.RETRY_DELAY) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.RETRY_DELAY, newValue, isValid.retryDelay)
        saveNotification.value?.show()
    }
})

const retryDelayMultiplier = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.RETRY_DELAY_MULTIPLIER) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(
            SETTINGS.RETRY_DELAY_MULTIPLIER,
            newValue,
            isValid.retryDelayMultiplier
        )
        saveNotification.value?.show()
    }
})

const selectiveRetryEnabled = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_ENABLED) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SELECTIVE_RETRY_ENABLED, newValue, true)
        saveNotification.value?.show()
    }
})

const selectiveRetryMaxAttempts = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_MAX_ATTEMPTS) as string,
    set: (newValue: string): void => {
        const parsed = Number.parseInt(newValue, 10)
        const inRange = Number.isFinite(parsed) && parsed >= 0 && parsed <= 2
        isValid.selectiveRetryMaxAttempts = inRange
        settingsStore.updateSetting(SETTINGS.SELECTIVE_RETRY_MAX_ATTEMPTS, newValue, inRange)
        saveNotification.value?.show()
    }
})

const selectiveRetryHighSeverityOnly = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_HIGH_SEVERITY_ONLY) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SELECTIVE_RETRY_HIGH_SEVERITY_ONLY, newValue, true)
        saveNotification.value?.show()
    }
})

const selectiveRetryScoreThreshold = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_SCORE_THRESHOLD) as string,
    set: (newValue: string): void => {
        const parsed = Number.parseInt(newValue, 10)
        const inRange = Number.isFinite(parsed) && parsed >= 0 && parsed <= 200
        isValid.selectiveRetryScoreThreshold = inRange
        settingsStore.updateSetting(SETTINGS.SELECTIVE_RETRY_SCORE_THRESHOLD, newValue, inRange)
        saveNotification.value?.show()
    }
})

const selectiveRetryImprovementMargin = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_IMPROVEMENT_MARGIN) as string,
    set: (newValue: string): void => {
        const parsed = Number.parseInt(newValue, 10)
        const inRange = Number.isFinite(parsed) && parsed >= 0 && parsed <= 100
        isValid.selectiveRetryImprovementMargin = inRange
        settingsStore.updateSetting(SETTINGS.SELECTIVE_RETRY_IMPROVEMENT_MARGIN, newValue, inRange)
        saveNotification.value?.show()
    }
})

const selectiveRetryProviderScope = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_PROVIDER_SCOPE) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SELECTIVE_RETRY_PROVIDER_SCOPE, newValue, true)
        saveNotification.value?.show()
    }
})

const selectiveRetryLogAttempts = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_LOG_ATTEMPTS) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.SELECTIVE_RETRY_LOG_ATTEMPTS, newValue, true)
        saveNotification.value?.show()
    }
})

const selectiveRetryGlossary = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_GLOSSARY) as string,
    set: (newValue: string): void => {
        const isValidJson = isJsonObject(newValue)
        isValid.selectiveRetryGlossary = isValidJson
        settingsStore.updateSetting(SETTINGS.SELECTIVE_RETRY_GLOSSARY, newValue, isValidJson)
        saveNotification.value?.show()
    }
})

const selectiveRetryProperNounLockEnabled = computed({
    get: (): string =>
        settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_PROPER_NOUN_LOCK_ENABLED) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(
            SETTINGS.SELECTIVE_RETRY_PROPER_NOUN_LOCK_ENABLED,
            newValue,
            true
        )
        saveNotification.value?.show()
    }
})

const selectiveRetryProtectedPatterns = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.SELECTIVE_RETRY_PROTECTED_PATTERNS) as string,
    set: (newValue: string): void => {
        const isValidPatterns = isJsonStringArray(newValue)
        isValid.selectiveRetryProtectedPatterns = isValidPatterns
        settingsStore.updateSetting(
            SETTINGS.SELECTIVE_RETRY_PROTECTED_PATTERNS,
            newValue,
            isValidPatterns
        )
        saveNotification.value?.show()
    }
})

function isJsonObject(value: string): boolean {
    try {
        const parsed = JSON.parse(value || '{}')
        return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)
    } catch {
        return false
    }
}

function isJsonStringArray(value: string): boolean {
    try {
        const parsed = JSON.parse(value || '[]')
        return Array.isArray(parsed) && parsed.every((item) => typeof item === 'string')
    } catch {
        return false
    }
}
</script>

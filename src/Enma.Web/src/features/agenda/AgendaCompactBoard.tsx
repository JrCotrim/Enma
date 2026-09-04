import { useState, type ReactNode } from 'react'
import {
  calendarDateFromLocalDate,
  localDateFromCalendarDate,
  parseCalendarDate,
  type CalendarDateParts,
} from './agendaDateTime'
import type { AgendaItem } from './agendaTypes'
import './agenda-compact.css'

const weekdayLabels = ['Dom', 'Seg', 'Ter', 'Qua', 'Qui', 'Sex', 'Sáb']
const agendaKinds: readonly AgendaItem['kind'][] = [
  'deadline',
  'task',
  'calendarEvent',
]

const fullDateFormatter = new Intl.DateTimeFormat('pt-BR', {
  weekday: 'long',
  day: 'numeric',
  month: 'long',
  year: 'numeric',
})

interface AgendaCompactBoardProps {
  readonly month: CalendarDateParts
  readonly dates: readonly string[]
  readonly itemsForDate: (dateKey: string) => readonly AgendaItem[]
  readonly isLoading: boolean
  readonly renderItem: (item: AgendaItem, dateKey: string) => ReactNode
}

function formatFullDate(dateKey: string): string {
  const parts = parseCalendarDate(dateKey)
  if (!parts) return dateKey
  return fullDateFormatter.format(localDateFromCalendarDate(parts))
}

function countLabel(count: number): string {
  return `${count} ${count === 1 ? 'compromisso' : 'compromissos'}`
}

export function AgendaCompactBoard({
  month,
  dates,
  itemsForDate,
  isLoading,
  renderItem,
}: AgendaCompactBoardProps) {
  const [selectedDate, setSelectedDate] = useState<string>()
  const [previewDate, setPreviewDate] = useState<string>()
  const todayDateKey = calendarDateFromLocalDate(new Date())
  const currentMonthDates = dates.filter((dateKey) => {
    const parts = parseCalendarDate(dateKey)
    return parts?.year === month.year && parts.month === month.month
  })
  const defaultDate =
    currentMonthDates.find(
      (dateKey) => dateKey === todayDateKey && itemsForDate(dateKey).length > 0,
    ) ??
    currentMonthDates.find((dateKey) => itemsForDate(dateKey).length > 0) ??
    (currentMonthDates.includes(todayDateKey) ? todayDateKey : undefined) ??
    currentMonthDates[0] ??
    dates[0]
  const pinnedDate = selectedDate ?? defaultDate
  const activeDate = previewDate ?? pinnedDate
  const activeItems = activeDate ? itemsForDate(activeDate) : []

  return (
    <div className="agenda-compact-board">
      <section
        className="agenda-compact-calendar"
        aria-label="Navegação mensal da agenda"
        aria-busy={isLoading}
      >
        <div className="agenda-compact-weekdays" aria-hidden="true">
          {weekdayLabels.map((label) => (
            <span key={label}>{label}</span>
          ))}
        </div>

        <div className="agenda-compact-grid">
          {dates.map((dateKey) => {
            const parts = parseCalendarDate(dateKey)
            if (!parts) return null

            const dayItems = itemsForDate(dateKey)
            const count = dayItems.length
            const isAdjacent =
              parts.year !== month.year || parts.month !== month.month
            const isToday = dateKey === todayDateKey
            const isSelected = dateKey === pinnedDate
            const isPreview = dateKey === activeDate && !isSelected
            const kinds = agendaKinds.filter((kind) =>
              dayItems.some((item) => item.kind === kind),
            )
            const fullDate = formatFullDate(dateKey)
            const ariaLabel = `${fullDate}, ${countLabel(count)}`

            return (
              <section
                className={`agenda-compact-day${isAdjacent ? ' is-adjacent' : ''}`}
                key={dateKey}
                aria-label={fullDate}
              >
                <button
                  className={`agenda-compact-day-button${count > 0 ? ' has-items' : ''}${isSelected ? ' is-selected' : ''}${isPreview ? ' is-preview' : ''}`}
                  type="button"
                  aria-label={ariaLabel}
                  aria-pressed={isSelected}
                  aria-current={isToday ? 'date' : undefined}
                  onClick={() => setSelectedDate(dateKey)}
                  onMouseEnter={() => setPreviewDate(dateKey)}
                  onMouseLeave={() =>
                    setPreviewDate((current) =>
                      current === dateKey ? undefined : current,
                    )
                  }
                  onFocus={() => setPreviewDate(dateKey)}
                  onBlur={() =>
                    setPreviewDate((current) =>
                      current === dateKey ? undefined : current,
                    )
                  }
                >
                  <time dateTime={dateKey} className="agenda-compact-day-number">
                    {parts.day}
                  </time>

                  {kinds.length > 0 ? (
                    <span className="agenda-compact-kinds" aria-hidden="true">
                      {kinds.map((kind) => (
                        <span
                          className={`agenda-compact-kind-dot agenda-compact-kind-dot-${kind}`}
                          key={kind}
                        />
                      ))}
                    </span>
                  ) : null}

                  {count > 0 ? (
                    <span className="agenda-compact-count" aria-hidden="true">
                      {count}
                    </span>
                  ) : null}
                </button>
              </section>
            )
          })}
        </div>
      </section>

      <section
        className="agenda-compact-panel"
        aria-label="Compromissos da data selecionada"
        aria-busy={isLoading}
      >
        <header className="agenda-compact-panel-header">
          <div>
            <p className="agenda-compact-panel-eyebrow">Compromissos</p>
            <h3 aria-live="polite">
              {activeDate ? formatFullDate(activeDate) : 'Selecione uma data'}
            </h3>
          </div>
          <span className="agenda-compact-panel-count">
            {countLabel(activeItems.length)}
          </span>
        </header>

        <div className="agenda-compact-schedule">
          {isLoading ? (
            <p className="agenda-compact-panel-state" role="status">
              Carregando compromissos...
            </p>
          ) : activeItems.length > 0 && activeDate ? (
            activeItems.map((item) => (
              <div
                className="agenda-compact-entry"
                key={`${activeDate}:${item.kind}:${item.id}`}
              >
                {renderItem(item, activeDate)}
              </div>
            ))
          ) : (
            <p className="agenda-compact-panel-state" role="status">
              Nenhum compromisso nesta data.
            </p>
          )}
        </div>
      </section>
    </div>
  )
}

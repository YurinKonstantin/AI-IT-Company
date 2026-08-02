# AI IT-Company — каталог функционала

Дата обновления: 2026-08-02

Краткий справочник «что умеет продукт». Практика работы — в [USER_GUIDE.md](USER_GUIDE.md).

---

## 1. Обзор архитектуры

| Элемент | Описание |
|---------|----------|
| UI | WinUI 3 desktop (`AI IT-Company`) |
| Оркестрация | `StagedPipeline` — режимы Create / Improve / Fix / Document / Analyze / PlanArchitecture |
| Агенты | Специализированные роли (Interpreter → … → Secretary) |
| Провайдеры | Ollama (локально), LM Studio, OpenRouter (облако), ONNX / Windows ML |
| Данные | `%USERPROFILE%\AiItCompany\` — проекты, логи, SQLite |
| CLI | `aiit run` (проект `Cli`) |

Ключевой принцип: локальный приоритет; облако — опционально.

---

## 2. Вкладки UI

| Вкладка | Tag | Назначение |
|---------|-----|------------|
| Чат | `chat` | Запуск конвейера, промпт, `@file`/`@folder`, лента событий |
| Дашборд | `dashboard` | Прогресс этапов, история сессий |
| Изменения | `changes` | Apply / Reject диффов (Improve/Fix) |
| Фриланс | `freelance` | Охота за задачами, score, Accept, оценка, статистика |
| Агенты | `agents` | Модель/источник на роль, пресеты Compact/Balanced |
| Модели | `models` | Локальные модели Ollama |
| Логи | `logs` | Журналы приложения |
| Настройки | `settings` | Провайдеры, биржа, ревью правок, редактор |
| Справка | `help` | Встроенный USER_GUIDE |

---

## 3. Режимы работы

| Режим (UI) | WorkMode | Нужна папка? | Основной путь | Артефакты |
|------------|----------|--------------|---------------|-----------|
| Авто | задаёт Interpreter | по задаче | классификация intent → маршрут | как у выбранного режима |
| Создать новый | CreateNew | нет | Architect → Scaffolder → этапы → Secretary | код, git, REPORT.md |
| Улучшить | Improve | да | Architect (дельта) → этапы (без scaffold) | патчи, REPORT |
| Исправить ошибку | FixError | да | Builder ↔ ErrorFixer | фиксы, build_log |
| Задокументировать | Document | да | Documenter → Secretary | README.md, ARCHITECTURE.md |
| Проанализировать | Analyze | да | Analyst → Secretary | ANALYSIS.md |
| Только ТЗ / архитектура | PlanArchitecture | нет | Architect → TZ.md → Secretary | TZ.md, план |

Типы проектов (`ProjectType`): WinUI, Api, Console, MonogameGame, Maui, WindowsService.

---

## 4. Агенты

| Роль | Когда | Типичная модель (Compact) |
|------|-------|---------------------------|
| Interpreter | всегда в начале | coder 7b, low T |
| Architect | Create/Improve/Plan | coder 7b |
| Scaffolder | CreateNew | без LLM (`dotnet new`) |
| Backend / Frontend / Fullstack | scope Backend/Frontend | coder 7b |
| Artist | scope Game | coder 7b + PNG |
| GameCoder | scope Game | coder 7b |
| Tester | scope Tests | coder 7b |
| Builder | после кодеров / Fix | restore/build/NuGet |
| ErrorFixer | красная сборка | coder 7b |
| Documenter / Analyst / UxReviewer | docs / analyze / UI | текстовые 3b–8b |
| Secretary | конец прогона | отчёт |

Пресеты: **Compact** (слабый ПК) / **Balanced** (14b на ключевых ролях) — страница Агенты.

---

## 5. MonoGame / Artist

| Функция | Где | На диске |
|---------|-----|----------|
| Генерация спрайтов | этап Game | `Content/Sprites/*.png` |
| Манифест | Artist | `Content/assets.manifest.json` |
| MGCB + инструкция | после Artist | `Content/Content.mgcb`, `Content/LOADING.md` |
| Режим картинок | Настройки | Procedural / OpenRouter / Ollama (+ fallback) |
| Загрузка в игре | GameCoder | `Texture2D.FromStream` + CopyToOutputDirectory |

---

## 6. Сборка и NuGet

| Функция | Где | Результат |
|---------|-----|-----------|
| `dotnet restore` / `build` | Builder | `build_ok`, `build_log` |
| Авто `dotnet add package` | Builder (до 3 попыток) | пакеты в csproj |
| Acceptance | после зелёной сборки | `dotnet test` при необходимости; smoke для MonoGame/службы |
| Терминал в ленте | события `terminal` | команды и stdout/stderr |

---

## 7. Биржа (фазы 7–8)

| Шаг | UI | Диск / БД |
|-----|-----|-----------|
| Охота | Фриланс → Охота | офферы в SQLite |
| Score | карточка + explain | feasibility, profit, bias |
| Accept | вручную / auto (opt-in) | `Output\Freelance\{id}\` + пайплайн |
| Оценка ★1–5 | низ формы | outcomes + калибровка EMA |
| Статистика | дашборд вкладки | win-rate, топ тегов, audit |

Источники: Demo, GitHub bounties, FL.ru/Kwork JSON feed. Авто-accept **не** шлёт отклик на биржу.

---

## 8. CLI

```text
aiit run --prompt "..." [--mode auto|create|improve|fix|document|analyze|plan] [--path <folder>]
```

| Код | Значение |
|-----|----------|
| 0 | успех |
| 1 | ошибка пайплайна |
| 2 | неверные аргументы |

---

## 9. Ограничения и безопасность

| Механизм | Суть |
|----------|------|
| ModeLocked | явный режим UI не перезаписывается Interpreter |
| FileWriteGuard | запись только внутри корня проекта |
| PendingChange / Review | Improve/Fix могут ждать Apply |
| Биржа auto | только внутренний пайплайн |
| Нет скрапинга | FL/Kwork — только настроенный feed URL |

---

## Функция → UI → диск (сводка)

| Функция | UI | Путь |
|---------|-----|------|
| Новый проект | Чат → Создать / Авто | `%USERPROFILE%\AiItCompany\Output\{id}\` |
| Сессия | Дашборд | таблица Sessions |
| Диффы | Изменения | файлы после Apply |
| Справка | Справка | этот гайд (встроенный) |
| БД / логи | — | `AiItCompany\aiitcompany.db`, `Logs\` |

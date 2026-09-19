import {
  getPasswordPolicyStatus,
  minimumPasswordLength,
} from './passwordPolicy'

interface PasswordRequirementsProps {
  readonly id: string
  readonly password: string
}

export function PasswordRequirements({
  id,
  password,
}: PasswordRequirementsProps) {
  const status = getPasswordPolicyStatus(password)
  const requirements = [
    {
      label: `${minimumPasswordLength} caracteres ou mais`,
      isMet: status.minimumLength,
    },
    {
      label: 'Letra maiúscula e minúscula',
      isMet: status.hasUppercase && status.hasLowercase,
    },
    {
      label: 'Um número',
      isMet: status.hasNumber,
    },
  ]
  const metCount = requirements.filter((requirement) => requirement.isMet).length
  const summary = password.length === 0
    ? 'Vazio'
    : metCount === requirements.length
      ? 'Todos os requisitos atendidos'
      : metCount === 0
        ? 'Nenhum requisito atendido'
        : `${metCount} de ${requirements.length} requisitos atendidos`

  return (
    <div id={id} className="password-requirements">
      <div
        className="password-strength-meter"
        role="progressbar"
        aria-label="Requisitos da senha atendidos"
        aria-valuemin={0}
        aria-valuemax={requirements.length}
        aria-valuenow={metCount}
        aria-valuetext={summary}
      >
        {requirements.map((requirement) => (
          <span
            key={requirement.label}
            className={
              requirement.isMet
                ? 'password-strength-segment is-met'
                : 'password-strength-segment'
            }
            aria-hidden="true"
          />
        ))}
      </div>
      <p
        className="password-requirements-summary"
        role="status"
        aria-live="polite"
        aria-atomic="true"
      >
        {summary}
      </p>
      <p className="password-requirements-title">Sua senha deve ter:</p>
      <ul>
        {requirements.map((requirement, index) => {
          const labelId = `${id}-requirement-${index}`

          return (
            <li
              key={requirement.label}
              className={requirement.isMet ? 'is-met' : undefined}
            >
              <input
                type="checkbox"
                className="password-requirement-checkbox"
                checked={requirement.isMet}
                disabled
                aria-labelledby={labelId}
              />
              <span id={labelId}>{requirement.label}</span>
            </li>
          )
        })}
      </ul>
      <p className="password-requirements-note">
        A senha também será verificada contra vazamentos conhecidos após o envio.
      </p>
    </div>
  )
}

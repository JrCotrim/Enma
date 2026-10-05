import { describe, expect, it } from 'vitest'
import { formatPhone } from './clientFormatting'

describe('formatPhone', () => {
  it.each([
    ['10-digit landline', '1133334444', '(11) 3333-4444'],
    ['11-digit mobile', '22999998888', '(22) 99999-8888'],
    ['country code + landline', '551133334444', '+55 (11) 3333-4444'],
    ['country code + mobile', '5522999998888', '+55 (22) 99999-8888'],
  ])('formats a %s', (_case, phone, formatted) => {
    expect(formatPhone(phone)).toBe(formatted)
  })

  it.each([
    ['too short', '12345678'],
    ['9 digits', '123456789'],
    ['12 digits without 55', '441133334444'],
    ['13 digits without 55', '4411999998888'],
    ['too long', '551199999888877'],
    ['non-digit legacy value', '(11) 3333-4444'],
    ['empty', ''],
  ])('returns a %s value unchanged', (_case, phone) => {
    expect(formatPhone(phone)).toBe(phone)
  })
})

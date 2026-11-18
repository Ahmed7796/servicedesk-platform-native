import { loginErrorMessage } from '../src/auth/loginErrors';

describe('loginErrorMessage', () => {
  it('401 is wrong credentials', () => {
    expect(loginErrorMessage(401, { title: 'Unauthorized', status: 401 })).toMatch(/wrong .*password/i);
  });

  it('400 shows the first field message', () => {
    expect(loginErrorMessage(400, { title: 'x', status: 400, errors: { Password: ['Password is required.'] } })).toBe(
      'Password is required.',
    );
  });

  it('400 falls back to detail, then to a fixed sentence', () => {
    expect(loginErrorMessage(400, { title: 'x', status: 400, detail: 'Bad input.' })).toBe('Bad input.');
    expect(loginErrorMessage(400, 'not json')).toMatch(/enter your email/i);
  });

  it('no status means the server cannot be reached', () => {
    expect(loginErrorMessage(undefined)).toMatch(/can't reach the server/i);
  });

  it('anything else is generic and never echoes the body', () => {
    const msg = loginErrorMessage(500, { stack: 'at Foo.bar (file.cs:1)', title: 'boom' });
    expect(msg).toBe('Something went wrong. Please try again.');
  });

  it('400 ignores a message over 200 characters and uses the fallback', () => {
    const long = 'x'.repeat(201);
    expect(loginErrorMessage(400, { errors: { A: [long] }, detail: long })).toMatch(/enter your email/i);
  });
});

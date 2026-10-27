// Scalar's "try it" calls go to web itself, which forwards them to api with the person's session (story 133). Like the
// dashboard, they carry web's CSRF header, without which web refuses any state-changing request.
export default {
  customFetch: (input, init) => {
    const request = new Request(input, init);
    if (new URL(request.url).origin === window.location.origin) {
      request.headers.set('X-CSRF', '1');
    }
    return fetch(request);
  },
};

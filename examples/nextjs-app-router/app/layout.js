export const metadata = { title: 'Receivify Next.js example' }

export default function RootLayout({ children }) {
  return (
    <html lang="en">
      <body style={{ font: '16px/1.5 system-ui, sans-serif', maxWidth: '32rem', margin: '4rem auto', padding: '0 1rem', color: '#18181b' }}>
        {children}
      </body>
    </html>
  )
}

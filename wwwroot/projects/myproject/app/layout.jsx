export const metadata = {
    title: 'My Next.js App on PackArcade',
    description: 'Full-stack app with Express + Next.js',
};

export default function RootLayout({ children }) {
    return (
        <html lang="en">
            <body style={{ margin: 0, background: '#0a0e1a', color: '#fff', fontFamily: 'sans-serif' }}>
                {children}
            </body>
        </html>
    );
}